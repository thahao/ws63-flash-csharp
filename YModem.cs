using System.IO.Ports;
using System.Text;
using Spectre.Console;

namespace AutoBurnCSharp
{
    /// <summary>
    /// YModem protocol implementation for file transfer
    /// </summary>
    public static class YModem
    {
        // Control Characters
        private const byte SOH = 0x01;   // Start of Header (128-byte blocks)
        private const byte STX = 0x02;   // Start of Text (1024-byte blocks)
        private const byte EOT = 0x04;   // End of Transmission
        private const byte ACK = 0x06;   // Acknowledge
        private const byte NAK = 0x15;   // Negative Acknowledge
        private const byte C = 0x43;     // 'C' character for CRC mode

        // Timeouts
        private const int YModemCTimeout = 5000;        // 5 seconds
        private const int YModemAckTimeout = 1500;      // 1.5 seconds
        private const int YModemXmitTimeout = 30000;    // 30 seconds

        /// <summary>
        /// Wait for ACK response from receiver
        /// </summary>
        /// <param name="serialPort">Serial port to read from</param>
        /// <returns>True if ACK received, false if NAK or timeout</returns>
        private static bool YModemWaitAck(SerialPort serialPort)
        {
            var startTime = DateTime.Now;

            while ((DateTime.Now - startTime).TotalMilliseconds < YModemAckTimeout)
            {
                if (serialPort.BytesToRead > 0)
                {
                    byte[] buffer = new byte[1];
                    int bytesRead = serialPort.Read(buffer, 0, 1);

                    if (bytesRead > 0)
                    {
                        if (buffer[0] == ACK)
                            return true;
                        if (buffer[0] == NAK)
                            return false;
                    }
                }

                Thread.Sleep(1); // Small delay to prevent CPU spinning
            }

            return false; // Timeout
        }

        /// <summary>
        /// Send a block with timeout and retry mechanism
        /// </summary>
        /// <param name="serialPort">Serial port to write to</param>
        /// <param name="block">Block data to send</param>
        /// <returns>True if successfully sent and acknowledged</returns>
        private static bool YModemBlockTimedXmit(SerialPort serialPort, byte[] block)
        {
            var startTime = DateTime.Now;

            while ((DateTime.Now - startTime).TotalMilliseconds < YModemXmitTimeout)
            {
                serialPort.Write(block, 0, block.Length);

                if (YModemWaitAck(serialPort))
                    return true;
            }

            return false; // Timeout
        }

        /// <summary>
        /// Transfer file using YModem protocol
        /// </summary>
        /// <param name="serialPort">Serial port for communication</param>
        /// <param name="filePath">Path to the firmware package file</param>
        /// <param name="binInfo">Binary information to transfer</param>
        /// <returns>True if transfer successful, false otherwise</returns>
        public static bool YModemXfer(SerialPort serialPort, string filePath, BinInfo binInfo)
        {
            try
            {
                uint fileSize = binInfo.Length;
                string fileName = binInfo.Name;
                uint offset = binInfo.Offset;
                uint totalBlocks = (fileSize + 1023) / 1024;
                uint lastBlockSize = fileSize % 1024;
                if (lastBlockSize == 0) lastBlockSize = 1024;

                AnsiConsole.MarkupLine($"[cyan]Starting YModem transfer: {fileName} ({fileSize} bytes, {totalBlocks} blocks)[/]");

                // Wait for 'C' to start CRC mode
                var startTime = DateTime.Now;
                bool cReceived = false;

                while ((DateTime.Now - startTime).TotalMilliseconds < YModemCTimeout)
                {
                    if (serialPort.BytesToRead > 0)
                    {
                        byte[] buffer = new byte[1];
                        int bytesRead = serialPort.Read(buffer, 0, 1);

                        if (bytesRead > 0 && buffer[0] == C)
                        {
                            cReceived = true;
                            break;
                        }
                    }
                    Thread.Sleep(1);
                }

                if (!cReceived)
                {
                    AnsiConsole.MarkupLine("[red]Timeout waiting for 'C' character[/]");
                    return false;
                }

                // Block 0: File Info
                byte[] block0 = new byte[133];
                block0[0] = SOH;  // SOH for 128-byte blocks
                block0[1] = 0x00; // Block number 0
                block0[2] = 0xFF; // Complement of block number

                // Add filename with null terminator
                byte[] fileNameBytes = Encoding.ASCII.GetBytes(fileName);
                Array.Copy(fileNameBytes, 0, block0, 3, Math.Min(fileNameBytes.Length, 127));
                
                // Add file size after null terminator
                int nameEndIndex = 3 + fileNameBytes.Length;
                block0[nameEndIndex] = 0; // Null terminator after filename
                nameEndIndex++;

                // Format file size as hex string (matching Python implementation)
                string sizeStr = "0x" + fileSize.ToString("X");
                byte[] sizeBytes = Encoding.ASCII.GetBytes(sizeStr);
                Array.Copy(sizeBytes, 0, block0, nameEndIndex, Math.Min(sizeBytes.Length, 130 - nameEndIndex));

                // Calculate CRC for data portion (bytes 3-130)
                byte[] dataBytes = new byte[128];
                Array.Copy(block0, 3, dataBytes, 0, 128);
                ushort crc = CRC.CalcCrc16(dataBytes);

                // Add CRC (big-endian)
                block0[131] = (byte)(crc >> 8);
                block0[132] = (byte)(crc & 0xFF);

                if (!YModemBlockTimedXmit(serialPort, block0))
                {
                    AnsiConsole.MarkupLine("[red]Failed to send file info block[/]");
                    return false;
                }

                // Data Blocks: File Data
                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                fileStream.Seek(offset, SeekOrigin.Begin);

                AnsiConsole.MarkupLine("[green]Transferring data blocks...[/]");
                
                // Track progress without interactive display
                int lastReportedPercent = -1;
                
                for (uint blockNum = 1; blockNum <= totalBlocks; blockNum++)
                {
                    // Show simple progress update every 10%
                    int currentPercent = (int)(blockNum * 100 / totalBlocks);
                    if (currentPercent / 10 > lastReportedPercent / 10)
                    {
                        AnsiConsole.Markup($"[green]{currentPercent}%.. [/]");
                        lastReportedPercent = currentPercent;
                    }
                    
                    byte[] dataBlock = new byte[1029]; // STX(1) + BlockNum(1) + ~BlockNum(1) + Data(1024) + CRC(2)
                    dataBlock[0] = STX;
                    dataBlock[1] = (byte)(blockNum % 256);
                    dataBlock[2] = (byte)(0xFF - dataBlock[1]);

                    uint currentBlockSize = (blockNum == totalBlocks) ? lastBlockSize : 1024;

                    // Read file data into a separate buffer
                    byte[] fileData = new byte[1024]; // Always 1024 bytes, even if last block is smaller
                    int bytesRead = fileStream.Read(fileData, 0, (int)currentBlockSize);

                    if (bytesRead != currentBlockSize)
                    {
                        AnsiConsole.MarkupLine($"[red]Error reading file data for block {blockNum}[/]");
                        return false;
                    }

                    // Pad the remaining bytes with zeros if this is the last block
                    if (bytesRead < 1024)
                    {
                        for (int i = bytesRead; i < 1024; i++)
                        {
                            fileData[i] = 0;
                        }
                    }

                    // Copy the file data to the data block
                    Array.Copy(fileData, 0, dataBlock, 3, 1024);

                    // Calculate CRC for 1024 bytes of data
                    ushort blockCrc = CRC.CalcCrc16(fileData);

                    // Add CRC (big-endian)
                    dataBlock[1027] = (byte)(blockCrc >> 8);
                    dataBlock[1028] = (byte)(blockCrc & 0xFF);

                    if (!YModemBlockTimedXmit(serialPort, dataBlock))
                    {
                        AnsiConsole.MarkupLine($"[red]Failed to send data block {blockNum}[/]");
                        return false;
                    }
                }
                
                AnsiConsole.WriteLine(); // New line after progress percentage

                // Send EOT and wait for ACK
                byte[] eotBytes = { EOT };
                serialPort.Write(eotBytes, 0, 1);

                // Retry sending EOT if no ACK received
                while (!YModemWaitAck(serialPort))
                {
                    serialPort.Write(eotBytes, 0, 1);
                }

                // Block 0: Finish Transmission (empty block)
                byte[] finishBlock = new byte[133];
                finishBlock[0] = SOH;
                finishBlock[1] = 0x00;
                finishBlock[2] = 0xFF;
                
                // Calculate CRC for empty data
                byte[] emptyData = new byte[128];
                ushort finishCrc = CRC.CalcCrc16(emptyData);
                finishBlock[131] = (byte)(finishCrc >> 8);
                finishBlock[132] = (byte)(finishCrc & 0xFF);

                if (!YModemBlockTimedXmit(serialPort, finishBlock))
                {
                    AnsiConsole.MarkupLine("[red]Failed to send finish block[/]");
                    return false;
                }

                AnsiConsole.MarkupLine($"[green]✓ Successfully transferred {fileName}[/]");
                return true;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error during YModem transfer: {ex.Message}[/]");
                return false;
            }
        }
    }
}
