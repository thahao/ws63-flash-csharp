using System.Text;
using Spectre.Console;

namespace AutoBurnCSharp
{
    /// <summary>
    /// Binary information structure within fwpkg file
    /// </summary>
    public class BinInfo
    {
        public string Name { get; set; } = string.Empty;
        public uint Offset { get; set; }
        public uint Length { get; set; }
        public uint BurnAddr { get; set; }
        public uint BurnSize { get; set; }
        public uint Type { get; set; }
    }

    /// <summary>
    /// Firmware package parser for .fwpkg files
    /// </summary>
    public class Fwpkg
    {
        private const int MaxPartitionCount = 16;
        private const uint ExpectedMagic = 0xefbeaddf;

        public uint Magic { get; private set; }
        public ushort Crc { get; private set; }
        public ushort Count { get; private set; }
        public uint Length { get; private set; }
        public List<BinInfo> BinInfos { get; private set; } = new();

        /// <summary>
        /// Initialize Fwpkg from file path
        /// </summary>
        /// <param name="filePath">Path to the .fwpkg file</param>
        public Fwpkg(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Firmware package file not found: {filePath}");

            ParseFwpkg(filePath);
        }

        /// <summary>
        /// Parse the firmware package file
        /// </summary>
        /// <param name="filePath">Path to the .fwpkg file</param>
        private void ParseFwpkg(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            // Read and validate header
            if (fs.Length < 12)
                throw new InvalidDataException("Error reading fwpkg header - file too small");

            Magic = reader.ReadUInt32();
            Crc = reader.ReadUInt16();
            Count = reader.ReadUInt16();
            Length = reader.ReadUInt32();

            // Validate magic number
            if (Magic != ExpectedMagic)
                throw new InvalidDataException($"Bad fwpkg file, invalid magic number. Expected: 0x{ExpectedMagic:x8}, Got: 0x{Magic:x8}");

            // Validate bin count
            if (Count > MaxPartitionCount)
                throw new InvalidDataException($"Bin count ({Count}) exceeds maximum partition count ({MaxPartitionCount})");

            // Read binary information structures
            const int binInfoSize = 32 + 5 * 4; // name[32] + 5 uint32 fields

            for (int i = 0; i < Count; i++)
            {
                if (fs.Position + binInfoSize > fs.Length)
                    throw new InvalidDataException($"Error reading fwpkg bin info for entry {i}");

                var binInfo = new BinInfo();

                // Read name (32 bytes, null-terminated string)
                byte[] nameBytes = reader.ReadBytes(32);
                int nullIndex = Array.IndexOf(nameBytes, (byte)0);
                if (nullIndex >= 0)
                    binInfo.Name = Encoding.UTF8.GetString(nameBytes, 0, nullIndex);
                else
                    binInfo.Name = Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');

                // Read remaining fields
                binInfo.Offset = reader.ReadUInt32();
                binInfo.Length = reader.ReadUInt32();
                binInfo.BurnAddr = reader.ReadUInt32();
                binInfo.BurnSize = reader.ReadUInt32();
                binInfo.Type = reader.ReadUInt32();

                BinInfos.Add(binInfo);
            }

            // Verify CRC
            fs.Seek(0, SeekOrigin.Begin);
            byte[] headerData = reader.ReadBytes((int)(12 + Count * binInfoSize));

            // CRC is calculated from byte 6 onward (skipping magic and CRC field itself)
            byte[] crcData = new byte[headerData.Length - 6];
            Array.Copy(headerData, 6, crcData, 0, crcData.Length);

            ushort calculatedCrc = CRC.CalcCrc16(crcData);
            if (calculatedCrc != Crc)
                throw new InvalidDataException($"Bad fwpkg file, CRC mismatch. Expected: 0x{Crc:x4}, Calculated: 0x{calculatedCrc:x4}");
        }

        /// <summary>
        /// Display firmware package information in a formatted table
        /// </summary>
        public void Show()
        {
            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.Title = new TableTitle("[bold yellow]Firmware Package Information[/]");

            // Add columns
            table.AddColumn(new TableColumn("[bold]F[/]").Centered());
            table.AddColumn(new TableColumn("[bold]BIN NAME[/]"));
            table.AddColumn(new TableColumn("[bold]BIN OFFSET[/]"));
            table.AddColumn(new TableColumn("[bold]BIN SIZE[/]"));
            table.AddColumn(new TableColumn("[bold]BURN ADDR[/]").RightAligned());
            table.AddColumn(new TableColumn("[bold]BURN SIZE[/]").RightAligned());
            table.AddColumn(new TableColumn("[bold]T[/]").Centered());

            // Add rows
            foreach (var binInfo in BinInfos)
            {
                string flashFlag = binInfo.Type == 0 ? "!" : "*";
                string flagColor = binInfo.Type == 0 ? "[red]![/]" : "[green]*[/]";

                table.AddRow(
                    flagColor,
                    $"[cyan]{binInfo.Name}[/]",
                    $"[yellow]0x{binInfo.Offset:08x}[/]",
                    $"[yellow]0x{binInfo.Length:08x}[/]",
                    $"[magenta]0x{binInfo.BurnAddr:08x}[/]",
                    $"[magenta]0x{binInfo.BurnSize:08x}[/]",
                    $"[blue]{binInfo.Type}[/]"
                );
            }

            AnsiConsole.Write(table);
        }

        /// <summary>
        /// Get loader boot binary information (type 0)
        /// </summary>
        /// <returns>Loader boot binary info or null if not found</returns>
        public BinInfo? GetLoaderBoot()
        {
            return BinInfos.FirstOrDefault(b => b.Type == 0);
        }

        /// <summary>
        /// Get application binaries (type 1)
        /// </summary>
        /// <returns>List of application binary infos</returns>
        public List<BinInfo> GetAppBinaries()
        {
            return BinInfos.Where(b => b.Type == 1).ToList();
        }
    }
}
