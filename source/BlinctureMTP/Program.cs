using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using ExifLib;
using Kurukuru;
using Nerven.CommandLineParser;
using Nerven.CommandLineParser.Extensions;
using Onova;
using Onova.Services;
using PortableDevices;

namespace BlinctureMTP
{
    // di "\\\\?\usb#vid_04e8&pid_6860&ms_comp_mtp&samsung_android#6&5c6399&0&0000#{6ac27878-a6fa-4155-ba85-f98f491d4f33}" "/ExtData/DCIM/Camera"
    // transfer "\\\\?\usb#vid_04e8&pid_6860&ms_comp_mtp&samsung_android#6&5c6399&0&0000#{6ac27878-a6fa-4155-ba85-f98f491d4f33}" "/ExtData/DCIM/OpenCamera" C:\Temp\BM
    public static class Program
    {
        // If files/directories on an Android device seems to be missing when browsing over MTP,
        // reset data/cache for system apps "External Storage" and "Media Storage", restart device, and wait a while.
        // https://android.stackexchange.com/a/134235

        public const int EXIT_CODE_OK = 0;
        public const int EXIT_CODE_UNKNOWN_ERROR = 1;
        public const int EXIT_CODE_UNKNOWN_COMMAND = 2;
        public const int EXIT_CODE_DEVICE_NOT_FOUND = 3;
        public const int EXIT_CODE_INVALID_DIRECTORY_PATH = 4;
        public const int EXIT_CODE_DEVICE_DIRECTORY_NOT_FOUND = 5;
        public const int EXIT_CODE_TARGET_DIRECTORY_NOT_FOUND = 6;

        private static readonly UTF8Encoding Utf8Encoding = new UTF8Encoding(false);

        public static int Main(params string[] args)
        {
            var commandLine = GetCommandLine(Environment.CommandLine, true);

            var exitCode = Main(commandLine);
            var waitForUser = commandLine.HasFlag("wait")
                              || (exitCode == EXIT_CODE_OK
                                  ? commandLine.HasFlag("waitOnSuccess")
                                  : commandLine.HasFlag("waitOnFail"));

            if (waitForUser)
                Console.ReadLine();

            return exitCode;
        }

        public static int Main(CommandLineItemCollection commandLine)
        {
            try
            {
                switch (commandLine.GetCommandOrDefault()?.Key)
                {
                    case "systeminfo":
                    case "si":
                        return SystemInfoCommand(commandLine);
                    case "deviceinfo":
                    case "di":
                        return DeviceInfoCommand(commandLine);
                    case "transfer":
                    case "t":
                        return TransferCommand(commandLine);
                    case "windowautosize":
                    case "was":
                        return WindowAutoSizeCommand();
                    case "instructionsonfile":
                    case "iof":
                        return InstructionsOnFileCommand(commandLine);
                    case "update":
                        return UpdateCommand();
                    case "interactive":
                    case "i":
                    case null:
                        return InteractiveCommand();
                    default:
                        Console.WriteLine($"[{nameof(EXIT_CODE_UNKNOWN_COMMAND)}]");
                        return EXIT_CODE_UNKNOWN_COMMAND;
                }
            }
            catch (Exception unhandledError)
            {
                Console.WriteLine($"[{nameof(EXIT_CODE_UNKNOWN_ERROR)}]");
                Console.WriteLine(unhandledError.ToString());
                return EXIT_CODE_UNKNOWN_ERROR;
            }
        }

        public static int SystemInfoCommand(CommandLineItemCollection commandLine)
        {
            PrintCommandName();

            var includeFriendlyName = commandLine.HasFlag("friendlyName");

            var devices = new PortableDeviceCollection();
            devices.Refresh();

            Console.WriteLine("[Devices]");
            foreach (var device in devices)
            {
                string deviceFriendlyName;
                if (includeFriendlyName)
                {
                    try
                    {
                        device.Connect();
                        deviceFriendlyName = device.FriendlyName;
                    }
                    finally
                    {
                        device.Disconnect();
                    }
                }
                else
                {
                    deviceFriendlyName = "-";
                }

                Console.WriteLine($"{deviceFriendlyName}\t{device.DeviceId}");
            }

            Console.WriteLine($"[{nameof(EXIT_CODE_OK)}]");
            return EXIT_CODE_OK;
        }

        public static int DeviceInfoCommand(CommandLineItemCollection commandLine)
        {
            int? exitCode;

            PrintCommandName();

            var deviceId = commandLine.GetArgument(0)?.Value;
            var directoryPath = commandLine.GetArgument(1)?.Value;

            PortableDevice device = default;
            try
            {
                PortableDeviceFolder directory;
                 (exitCode, device, directory) = ConnectAndGetDeviceAndDirectory(deviceId, directoryPath);

                if (exitCode.HasValue)
                    return exitCode.Value;

                PrintDirectory(directory);
            }
            finally
            {
                device?.Disconnect();
            }
            
            Console.WriteLine($"[{nameof(EXIT_CODE_OK)}]");
            return EXIT_CODE_OK;
        }

        public static int TransferCommand(CommandLineItemCollection commandLine)
        {
            int? exitCode;
            PrintCommandName();

            var deviceId = commandLine.GetArgument(0)?.Value;
            var sourceDirectoryPath = commandLine.GetArgument(1)?.Value;
            var targetDirectoryPath = commandLine.GetArgument(2)?.Value;

            if (string.IsNullOrEmpty(targetDirectoryPath) || !Directory.Exists(targetDirectoryPath))
            {
                Console.WriteLine($"[{nameof(EXIT_CODE_TARGET_DIRECTORY_NOT_FOUND)}]");
                return EXIT_CODE_TARGET_DIRECTORY_NOT_FOUND;
            }
            
            var tempTargetDirectoryPath = Path.Combine(targetDirectoryPath, $".{nameof(BlinctureMTP)}-temp", Guid.NewGuid().ToString("N"));
            SetHidden(CreateDirectoryIfMissing(tempTargetDirectoryPath));

            PortableDevice device = default;
            PortableDeviceFolder directory;
            try
            {
                (exitCode, device, directory) = ConnectAndGetDeviceAndDirectory(deviceId, sourceDirectoryPath);
                if (exitCode.HasValue)
                    return exitCode.Value;
                
                foreach (var file in directory.Files.OfType<PortableDeviceFile>())
                {
                    Spinner.Start($"Transfering '{file.Name}' ...", spinner =>
                    {
                        var flagTargetFilePath = Path.Combine(targetDirectoryPath, $".{nameof(BlinctureMTP)}-flags", file.Name);
                        SetHidden(CreateContainingDirectoryIfMissing(flagTargetFilePath));
                        if (File.Exists(flagTargetFilePath))
                        {
                            spinner.Info("File already transferred.");
                            return;
                        }

                        var tempTargetFilePath = Path.Combine(tempTargetDirectoryPath, file.Name);
                        string targetFileName;
                        using (var tempTargetFileStream = new FileStream(tempTargetFilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
                        {
                            var data = device.DownloadFileToStream(file);
                            tempTargetFileStream.Write(data, 0, data.Length);

                            tempTargetFileStream.Position = 0;
                            targetFileName = GetTargetFileName(commandLine, file.Name, tempTargetFileStream);
                            tempTargetFileStream.Close();
                        }

                        var targetFilePath = Path.Combine(targetDirectoryPath, targetFileName);
                        CreateContainingDirectoryIfMissing(targetFilePath);
                        File.Move(tempTargetFilePath, targetFilePath);

                        File.WriteAllText(flagTargetFilePath, DateTimeOffset.Now.ToString("O"), Utf8Encoding);
                        spinner.Succeed($"Transferred '{file.Name}' as '{targetFileName}'.");
                    });
                }
            }
            finally
            {
                device?.Disconnect();
            }
            
            Directory.Delete(tempTargetDirectoryPath);
            
            Console.WriteLine($"[{nameof(EXIT_CODE_OK)}]");
            return EXIT_CODE_OK;
        }

        private static string GetTargetFileName(CommandLineItemCollection commandLine, string targetFileName, FileStream fileStream)
        {
            var dateTimePattern = commandLine.GetOptionOrDefault("dateTimePattern")?.Value ?? "yyyy\\/MM\\/yyyy\\-MM\\-dd\\_HH\\-mm\\-ss";
            switch (Path.GetExtension(targetFileName)?.ToLowerInvariant())
            {
                case ".jpg":
                case ".jpeg":
                    using (var reader = new ExifReader(fileStream, true))
                    {
                        if (reader.GetTagValue(ExifTags.DateTimeDigitized, out DateTime timestampPictureTaken))
                            return $"{timestampPictureTaken.ToString(dateTimePattern, CultureInfo.InvariantCulture)}.jpg";

                        return targetFileName;
                    }
                default:
                    return targetFileName;
            }
        }

        private static int WindowAutoSizeCommand()
        {
            Console.WindowWidth = Math.Max(Console.WindowWidth, (int)(Console.LargestWindowWidth * 0.8));
            Console.BufferWidth = Math.Max(Console.BufferWidth, Console.WindowWidth);
            Console.WindowHeight = Math.Max(Console.WindowHeight, (int)(Console.LargestWindowHeight * 0.8));
            Console.BufferHeight = Math.Max(Console.BufferHeight, 4096);

            Console.WriteLine($"[{nameof(EXIT_CODE_OK)}]");
            return EXIT_CODE_OK;
        }

        private static int InstructionsOnFileCommand(CommandLineItemCollection commandLine)
        {
            var commands = new List<CommandLineItemCollection>();

            Console.WriteLine("[Files]");
            foreach (var instructionsFilePath in commandLine.GetArguments().Select(argument => argument.Value))
            {
                var fullPath = Path.GetFullPath(instructionsFilePath);

                Console.WriteLine($"\t{fullPath}\t{instructionsFilePath}");
                foreach (var line in File.ReadAllLines(fullPath, Utf8Encoding))
                {
                    commands.Add(GetCommandLine(line));
                }
            }

            Console.WriteLine("[Commands]");
            foreach (var command in commands)
            {
                Console.WriteLine($"> {string.Join(" ", command)}");
                var exitCode = Main(command);
                Console.WriteLine($"< {exitCode}");

                if (exitCode != EXIT_CODE_OK)
                    return exitCode;
            }
            
            Console.WriteLine($"[{nameof(EXIT_CODE_OK)}]");
            return EXIT_CODE_OK;
        }

        public static int UpdateCommand()
        {
            PrintCommandName();

            var manager = new UpdateManager(
                new GithubPackageResolver(
                    ConfigurationManager.AppSettings["SelfUpdateGitHubUser"],
                    ConfigurationManager.AppSettings["SelfUpdateGitHubRepo"],
                    $"{nameof(BlinctureMTP)}.zip"),
                new ZipPackageExtractor());

            return Task.Run(async () =>
            {
                var exitCode = EXIT_CODE_UNKNOWN_ERROR;
                await Spinner.StartAsync("Checking for updates ...", async spinner =>
                {
                    try
                    {
                        var status = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
                        if (status.CanUpdate)
                        {
                            spinner.Text = $"Found new version '{status.LastVersion}'.";
                            await manager.PrepareUpdateAsync(status.LastVersion).ConfigureAwait(false);
                            manager.LaunchUpdater(status.LastVersion, false);
                            exitCode = EXIT_CODE_OK;
                        }
                        else
                        {
                            manager.Cleanup();
                            spinner.Info("No updates found.");
                            exitCode = EXIT_CODE_OK;
                        }
                    }
                    catch
                    {
                        spinner.Fail("Update failed.");
                        exitCode = EXIT_CODE_UNKNOWN_ERROR;
                        throw;
                    }
                }).ConfigureAwait(false);

                return exitCode;
            }).Result;
        }

        public static int InteractiveCommand()
        {
            PrintCommandName();

            while (true)
            {
                Console.WriteLine(">");
                var interactiveArgs = GetCommandLine(Console.ReadLine());

                if (new[] { "quit", "exit", ":q", ":e" }.Contains(interactiveArgs.GetCommandOrDefault()?.Key, StringComparer.Ordinal))
                {
                    Console.WriteLine($"[{nameof(EXIT_CODE_OK)}]");
                    return EXIT_CODE_OK;
                }

                var exitCode = Main(interactiveArgs);
                Console.WriteLine($"< {exitCode}");
            }
        }

        private static void PrintCommandName([CallerMemberName] string callerMemberName = null)
        {
            var suffix = "Command";
            var commandName = callerMemberName?.EndsWith(suffix, StringComparison.Ordinal) == true
                ? callerMemberName.Substring(0, callerMemberName.Length - suffix.Length)
                : callerMemberName;
            Console.WriteLine($"# {commandName} #");
        }

        private static CommandLineItemCollection GetCommandLine(string commandLineString, bool skipFirst = false)
        {
            var splitted = CommandLineSplitter.WindowsCompatible.ParseString(commandLineString);
            if (skipFirst)
            {
                if (splitted.Parts.Count <= 1)
                    return new CommandLineItemCollection(new List<CommandLineItem>());

                splitted = splitted.Slice(1, splitted.Parts.Count - 1);
            }

            return StandardCommandLineParser.Default.ParseCommandLine(splitted);
        }

        private static void SetHidden(string path)
        {
            if (path != null)
                File.SetAttributes(path, FileAttributes.Hidden);
        }

        private static string CreateContainingDirectoryIfMissing(string filePath)
        {
            return CreateDirectoryIfMissing(Path.GetDirectoryName(filePath));
        }

        private static string CreateDirectoryIfMissing(string directoryPath)
        {
            if (directoryPath != null && !Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
                return directoryPath;
            }

            return null;
        }

        public static (int?, PortableDevice, PortableDeviceFolder) ConnectAndGetDeviceAndDirectory(
            string deviceId,
            string directoryPath)
        {
            int? exitCode;
            PortableDevice device;

            (exitCode, device) = GetDevice(deviceId);
            if (exitCode.HasValue)
                return (exitCode.Value, null, null);

            PortableDeviceFolder directory = default;
            Spinner.Start("Connecting to device ...", spinner =>
            {
                device.Connect();

                spinner.Text = "Listing contents ...";
                var rootDirectory = device.GetContents();

                (exitCode, directory) = GetDirectory(rootDirectory, directoryPath);

                if (exitCode.HasValue)
                    spinner.Fail("Failed to list contents");
                else
                    spinner.Succeed("Connected and contents listed");
            });

            if (exitCode.HasValue)
                return (exitCode.GetValueOrDefault(EXIT_CODE_UNKNOWN_COMMAND), null, null);

            return (null, device, directory);
        }

        public static (int?, PortableDevice) GetDevice(string deviceId)
        {
            var devices = new PortableDeviceCollection();
            devices.Refresh();
            var deviceCollection = devices.ToList();
            var device = deviceCollection.SingleOrDefault(d => string.Equals(deviceId, d.DeviceId, StringComparison.Ordinal));
            if (device == null)
            {
                Console.WriteLine($"[{nameof(EXIT_CODE_DEVICE_NOT_FOUND)}]");
                return (EXIT_CODE_DEVICE_NOT_FOUND, null);
            }

            return (null, device);
        }

        private static (int?, PortableDeviceFolder) GetDirectory(PortableDeviceFolder rootDirectory, string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath) || (directoryPath[0] != Path.PathSeparator && directoryPath[0] != Path.AltDirectorySeparatorChar))
            {
                Console.WriteLine($"[{nameof(EXIT_CODE_INVALID_DIRECTORY_PATH)}]");
                return (EXIT_CODE_INVALID_DIRECTORY_PATH, null);
            }

            var endsWithPathSeparator = directoryPath[directoryPath.Length - 1] == Path.PathSeparator || directoryPath[directoryPath.Length - 1] == Path.AltDirectorySeparatorChar;
            var directoryPathParts = (endsWithPathSeparator ? directoryPath.Substring(0, directoryPath.Length - 1) : directoryPath)
                .Split(Path.PathSeparator, Path.AltDirectorySeparatorChar);

            var directory = rootDirectory;
            foreach (var directoryPathPart in directoryPathParts.Skip(1))
            {
                directory = directory.Files
                    .OfType<PortableDeviceFolder>()
                    .SingleOrDefault(entry => string.Equals(entry.Name, directoryPathPart, StringComparison.Ordinal));

                if (directory == null)
                {
                    Console.WriteLine($"[{nameof(EXIT_CODE_DEVICE_DIRECTORY_NOT_FOUND)}]");
                    return (EXIT_CODE_DEVICE_DIRECTORY_NOT_FOUND, null);
                }
            }

            return (null, directory);
        }

        private static void PrintDirectory(PortableDeviceFolder directory)
        {
            Console.WriteLine("[Directory]");
            Console.WriteLine($"{directory.Name}/\t{directory.Id}");
            foreach (var entry in directory.Files)
            {
                Console.WriteLine($"\t{entry.Name}{(entry is PortableDeviceFolder ? "/" : string.Empty)}\t{entry.Id}");
            }
        }
    }
}
