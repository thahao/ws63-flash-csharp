using System.IO.Ports;
using Spectre.Console;

namespace AutoBurnCSharp
{
    /// <summary>
    /// Command definitions for WS63 communication
    /// </summary>
    public class CmdDef
    {
        public byte Cmd { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public int Length { get; set; }
    }

    /// <summary>
    /// WS63 firmware burning tool implementation
    /// </summary>
    public class Ws63BurnTools
    {
        // Constants
        private const int ResetTimeout = 10000;     // 10 seconds
        private const int UartReadTimeout = 5000;   // 5 seconds
        private const byte CmdHandshake = 0;
        private const byte CmdDownload = 1;
        private const byte CmdReset = 2;

        // Available baud rates
        private static readonly int[] AvailableBaudRates =
        {
            115200, 230400, 460800, 500000, 576000, 921600,
            1000000, 1152000, 1500000, 2000000
        };

        // Command definitions
        private static readonly CmdDef[] Ws63eFlashInfo =
        {
            new() // CMD_HANDSHAKE
            {
                Cmd = 0xf0,
                Data = new byte[] { 0x00, 0xc2, 0x01, 0x00, 0x08, 0x01, 0x00, 0x00 },
                Length = 8
            },
            new() // CMD_DOWNLOAD
            {
                Cmd = 0xd2,
                Data = new byte[]
                {
                    0x00, 0x00, 0x00, 0x00,  // ADDR
                    0x00, 0x00, 0x00, 0x00,  // ILEN
                    0xFF, 0xFF, 0xFF, 0xFF,  // ERAS
                    0x00, 0xFF               // CONST
                },
                Length = 14
            },
            new() // CMD_RST
            {
                Cmd = 0x87,
                Data = new byte[] { 0x00, 0x00 },
                Length = 2
            }
        };

        private readonly string _portName;
        private readonly int _baudRate;
        private SerialPort? _serialPort;

        public Ws63BurnTools(string portName, int baudRate)
        {
            _portName = portName;
            _baudRate = baudRate;

            if (!AvailableBaudRates.Contains(baudRate))
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Baud rate {baudRate} is not in the recommended list[/]");
            }
        }

        /// <summary>
        /// Send command definition to device
        /// </summary>
        /// <param name="cmdDef">Command definition to send</param>
        private void Ws63SendCmdDef(CmdDef cmdDef)
        {
            if (_serialPort == null)
                throw new InvalidOperationException("Serial port not initialized");

            int totalBytes = cmdDef.Length + 10;
            byte[] buffer = new byte[totalBytes];

            // Magic number (0xdeadbeef in little-endian)
            buffer[0] = 0xef;
            buffer[1] = 0xbe;
            buffer[2] = 0xad;
            buffer[3] = 0xde;

            // Frame length (little-endian)
            buffer[4] = (byte)(totalBytes & 0xFF);
            buffer[5] = (byte)((totalBytes >> 8) & 0xFF);

            // Command and inverted command
            buffer[6] = cmdDef.Cmd;
            buffer[7] = (byte)(cmdDef.Cmd ^ 0xFF);

            // Copy command data
            Array.Copy(cmdDef.Data, 0, buffer, 8, cmdDef.Length);

            // Calculate and add CRC
            byte[] crcData = new byte[totalBytes - 2];
            Array.Copy(buffer, 0, crcData, 0, crcData.Length);
            ushort crc = CRC.CalcCrc16(crcData);

            buffer[8 + cmdDef.Length] = (byte)(crc & 0xFF);
            buffer[8 + cmdDef.Length + 1] = (byte)((crc >> 8) & 0xFF);

            AnsiConsole.MarkupLine($"[dim]> {Convert.ToHexString(buffer)}[/]");

            _serialPort.Write(buffer, 0, totalBytes);
        }

        /// <summary>
        /// Read data until magic number is found
        /// </summary>
        /// <returns>0 on success, -1 on error</returns>
        private int UartReadUntilMagic()
        {
            if (_serialPort == null)
                throw new InvalidOperationException("Serial port not initialized");

            byte[] buffer = new byte[1036]; // 1024 + 12 for header
            byte[] magic = { 0xef, 0xbe, 0xad, 0xde };
            int i = 0;
            int frameLen = 0;
            int state = 0;
            var startTime = DateTime.Now;

            while (true)
            {
                // Check timeout
                if ((DateTime.Now - startTime).TotalMilliseconds > UartReadTimeout)
                {
                    AnsiConsole.MarkupLine("[red]uart_read_until_magic: Timeout[/]");
                    return -1;
                }

                try
                {
                    if (_serialPort.BytesToRead > 0)
                    {
                        byte[] singleByte = new byte[1];
                        int bytesRead = _serialPort.Read(singleByte, 0, 1);

                        if (bytesRead == 0)
                            continue;

                        // Reset timeout on valid data
                        startTime = DateTime.Now;
                        byte currentByte = singleByte[0];
                        buffer[i] = currentByte;

                        if (state == 0)
                        {
                            // Looking for magic sequence
                            if (magic[i] == buffer[i])
                            {
                                i++;
                                if (i >= 4)
                                {
                                    state = 1;
                                }
                                continue;
                            }
                            else
                            {
                                i = 0;
                            }
                        }
                        else if (state == 1)
                        {
                            if (i == 5) // Bytes 4:5 define frame length
                            {
                                frameLen = buffer[4] | (buffer[5] << 8);
                            }
                            else if (i == frameLen - 1) // Reached end of frame
                            {
                                break;
                            }
                            i++;
                        }
                    }
                    else
                    {
                        Thread.Sleep(1); // Small delay to prevent CPU spinning
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]uart_read_until_magic: {ex.Message}[/]");
                    return -1;
                }
            }

            // Check CRC
            AnsiConsole.MarkupLine($"[dim]< {Convert.ToHexString(buffer, 0, i + 1)}[/]");

            if (frameLen >= 2)
            {
                ushort crcReceived = (ushort)(buffer[frameLen - 2] | (buffer[frameLen - 1] << 8));

                byte[] crcData = new byte[frameLen - 2];
                Array.Copy(buffer, 0, crcData, 0, crcData.Length);
                ushort crcCalculated = CRC.CalcCrc16(crcData);

                if (crcReceived != crcCalculated)
                {
                    AnsiConsole.MarkupLine("[yellow]Warning: bad CRC from frame![/]");
                    return -1;
                }
            }

            return 0;
        }

        /// <summary>
        /// Flash firmware to device
        /// </summary>
        /// <param name="firmwarePath">Path to firmware package file</param>
        /// <returns>True if successful, false otherwise</returns>
        public bool Flash(string firmwarePath)
        {
            try
            {
                // Initialize serial port
                _serialPort = new SerialPort(_portName, 115200)
                {
                    ReadTimeout = 1000,
                    WriteTimeout = 1000
                };

                _serialPort.Open();
                _serialPort.RtsEnable = false;

                // Parse firmware package
                var fwpkg = new Fwpkg(firmwarePath);
                var loaderBoot = fwpkg.GetLoaderBoot();

                if (loaderBoot == null)
                {
                    AnsiConsole.MarkupLine("[red]Required loaderboot not found in fwpkg![/]");
                    return false;
                }

                // Display firmware information
                fwpkg.Show();

                // Stage 1: Flash loaderboot
                AnsiConsole.MarkupLine("[cyan]Waiting for device reset...[/]");

                var startTime = DateTime.Now;
                bool handshakeSuccess = false;

                while ((DateTime.Now - startTime).TotalMilliseconds < ResetTimeout)
                {
                    // Set baud rate in handshake command
                    byte[] baudRateBytes = BitConverter.GetBytes((uint)_baudRate);
                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Copy(baudRateBytes, 0, Ws63eFlashInfo[CmdHandshake].Data, 0, 4);
                    }
                    else
                    {
                        // Convert to little-endian if needed
                        Array.Reverse(baudRateBytes);
                        Array.Copy(baudRateBytes, 0, Ws63eFlashInfo[CmdHandshake].Data, 0, 4);
                    }

                    // Send handshake
                    Ws63SendCmdDef(Ws63eFlashInfo[CmdHandshake]);

                    // Check for ACK response
                    Thread.Sleep(7); // Give device time to respond

                    if (_serialPort.BytesToRead > 0)
                    {
                        byte[] responseBuffer = new byte[_serialPort.BytesToRead];
                        _serialPort.Read(responseBuffer, 0, responseBuffer.Length);

                        byte[] expectedAck = { 0xEF, 0xBE, 0xAD, 0xDE, 0x0C, 0x00, 0xE1, 0x1E };

                        // Check if ACK is present in response
                        for (int i = 0; i <= responseBuffer.Length - expectedAck.Length; i++)
                        {
                            bool found = true;
                            for (int j = 0; j < expectedAck.Length; j++)
                            {
                                if (responseBuffer[i + j] != expectedAck[j])
                                {
                                    found = false;
                                    break;
                                }
                            }

                            if (found)
                            {
                                // Switch to target baud rate
                                _serialPort.BaudRate = _baudRate;
                                AnsiConsole.MarkupLine("[green]Establishing ymodem session...[/]");
                                handshakeSuccess = true;
                                break;
                            }
                        }

                        if (handshakeSuccess)
                            break;
                    }
                }

                if (!handshakeSuccess)
                {
                    AnsiConsole.MarkupLine("[red]Timeout while waiting for device reset[/]");
                    return false;
                }

                Thread.Sleep(500);

                // Transfer loaderboot using YModem
                AnsiConsole.MarkupLine($"[cyan]Transferring {loaderBoot.Name}...[/]");

                if (!YModem.YModemXfer(_serialPort, firmwarePath, loaderBoot))
                {
                    AnsiConsole.MarkupLine($"[red]Error transferring {loaderBoot.Name}[/]");
                    return false;
                }

                UartReadUntilMagic();

                // Stage 2: Transfer application binaries
                var appBinaries = fwpkg.GetAppBinaries();

                foreach (var binInfo in appBinaries)
                {
                    AnsiConsole.MarkupLine($"[cyan]Transferring {binInfo.Name}...[/]");

                    // Calculate erase size (round up to 8192-byte boundary)
                    uint eraseSize = (uint)(Math.Ceiling(binInfo.Length / 8192.0) * 0x2000);

                    // Prepare download command
                    var downloadCmd = Ws63eFlashInfo[CmdDownload];
                    byte[] newData = new byte[downloadCmd.Data.Length];
                    Array.Copy(downloadCmd.Data, newData, downloadCmd.Data.Length);

                    // Set burn address (little-endian)
                    byte[] addrBytes = BitConverter.GetBytes(binInfo.BurnAddr);
                    if (!BitConverter.IsLittleEndian) Array.Reverse(addrBytes);
                    Array.Copy(addrBytes, 0, newData, 0, 4);

                    // Set length (little-endian)
                    byte[] lenBytes = BitConverter.GetBytes(binInfo.Length);
                    if (!BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                    Array.Copy(lenBytes, 0, newData, 4, 4);

                    // Set erase size (little-endian)
                    byte[] eraseBytes = BitConverter.GetBytes(eraseSize);
                    if (!BitConverter.IsLittleEndian) Array.Reverse(eraseBytes);
                    Array.Copy(eraseBytes, 0, newData, 8, 4);

                    var modifiedCmd = new CmdDef
                    {
                        Cmd = downloadCmd.Cmd,
                        Data = newData,
                        Length = downloadCmd.Length
                    };

                    Ws63SendCmdDef(modifiedCmd);
                    UartReadUntilMagic();

                    if (!YModem.YModemXfer(_serialPort, firmwarePath, binInfo))
                    {
                        AnsiConsole.MarkupLine($"[red]Error transferring {binInfo.Name}[/]");
                        return false;
                    }

                    Thread.Sleep(100);
                }

                // Reset device
                AnsiConsole.MarkupLine("[cyan]Done. Resetting device...[/]");
                Ws63SendCmdDef(Ws63eFlashInfo[CmdReset]);
                UartReadUntilMagic();

                AnsiConsole.MarkupLine("[green]✓ Firmware flash completed successfully![/]");
                return true;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error during flash process: {ex.Message}[/]");
                return false;
            }
            finally
            {
                _serialPort?.Close();
                _serialPort?.Dispose();
            }
        }
    }
}
