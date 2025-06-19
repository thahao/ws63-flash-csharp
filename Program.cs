using Spectre.Console;
using System;
using System.CommandLine;

namespace AutoBurnCSharp
{
    /// <summary>
    /// Main program entry point
    /// </summary>
    internal class Program
    {
        static async Task<int> Main(string[] args)
        {
            // Create root command
            var rootCommand = new RootCommand("WS63 firmware burning tool written in C#");

            // Create options
            var verboseOption = new Option<bool>(
                aliases: new[] { "--verbose", "-v" },
                description: "Print debug information");

            var portOption = new Option<string>(
                aliases: new[] { "--port", "-p" },
                description: "Specify serial port (e.g., COM3 on Windows, /dev/ttyUSB0 on Linux)")
            { IsRequired = false };

            var baudrateOption = new Option<int>(
                aliases: new[] { "--baudrate", "-b" },
                description: "Set serial port baud rate",
                getDefaultValue: () => 921600);

            var showOption = new Option<bool>(
                aliases: new[] { "--show", "-s" },
                description: "Only show firmware information");

            var firmwareArgument = new Argument<FileInfo>(
                name: "firmware-file",
                description: "Path to the firmware package (.fwpkg) file")
            {
                Arity = ArgumentArity.ExactlyOne
            };

            // Add options and argument to root command
            rootCommand.AddOption(verboseOption);
            rootCommand.AddOption(portOption);
            rootCommand.AddOption(baudrateOption);
            rootCommand.AddOption(showOption);
            rootCommand.AddArgument(firmwareArgument);

            // Set command handler
            rootCommand.SetHandler(async (verbose, port, baudrate, show, firmwareFile) =>
            {
                try
                {
                    // Display banner
                    var banner = new FigletText("AutoBurn C#")
                        .LeftJustified()
                        .Color(Color.Cyan1);
                    AnsiConsole.Write(banner);

                    AnsiConsole.MarkupLine("[dim]WS63 firmware burning tool - C# version 0.3.0[/]");
                    AnsiConsole.WriteLine();

                    // Validate firmware file
                    if (!firmwareFile.Exists)
                    {
                        AnsiConsole.MarkupLine($"[red]Error: Firmware file not found: {firmwareFile.FullName}[/]");
                        Environment.Exit(1);
                        return;
                    }

                    if (!firmwareFile.Name.EndsWith(".fwpkg", StringComparison.OrdinalIgnoreCase))
                    {
                        AnsiConsole.MarkupLine("[yellow]Warning: File doesn't have .fwpkg extension[/]");
                    }

                    // Show mode - just display firmware information
                    if (show)
                    {
                        try
                        {
                            var fwpkg = new Fwpkg(firmwareFile.FullName);
                            fwpkg.Show();
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]Error reading firmware package: {ex.Message}[/]");
                            Environment.Exit(1);
                        }
                        return;
                    }

                    // Flash mode - requires port
                    if (string.IsNullOrEmpty(port))
                    {
                        AnsiConsole.MarkupLine("[red]Error: Serial port must be specified with -p or --port option[/]");
                        AnsiConsole.MarkupLine("[yellow]Example: AutoBurnCSharp.exe firmware.fwpkg -p COM3[/]");
                        Environment.Exit(1);
                        return;
                    }

                    // Validate baud rate
                    var validBaudRates = new[] { 115200, 230400, 460800, 500000, 576000, 921600, 1000000, 1152000, 1500000, 2000000 };
                    if (!validBaudRates.Contains(baudrate))
                    {
                        AnsiConsole.MarkupLine($"[yellow]Warning: Baud rate {baudrate} is not in the recommended list[/]");
                        AnsiConsole.MarkupLine($"[yellow]Recommended rates: {string.Join(", ", validBaudRates)}[/]");
                    }

                    if (verbose)
                    {
                        AnsiConsole.MarkupLine($"[dim]Firmware file: {firmwareFile.FullName}[/]");
                        AnsiConsole.MarkupLine($"[dim]Serial port: {port}[/]");
                        AnsiConsole.MarkupLine($"[dim]Baud rate: {baudrate}[/]");
                        AnsiConsole.WriteLine();
                    }

                    // Create burn tool and flash firmware
                    var burnTool = new Ws63BurnTools(port, baudrate);

                    AnsiConsole.Status()
                        .Start("Initializing...", ctx =>
                        {
                            ctx.SpinnerStyle(Style.Parse("green"));

                            bool success = burnTool.Flash(firmwareFile.FullName);

                            if (!success)
                            {
                                AnsiConsole.MarkupLine("[red]✗ Firmware flash failed![/]");
                                Environment.Exit(1);
                            }
                        });
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Unexpected error: {ex.Message}[/]");
                    if (verbose)
                    {
                        AnsiConsole.MarkupLine($"[dim]{ex.StackTrace}[/]");
                    }
                    Environment.Exit(1);
                }
            }, verboseOption, portOption, baudrateOption, showOption, firmwareArgument);

            // Parse and invoke
            return await rootCommand.InvokeAsync(args);
        }
    }
}
