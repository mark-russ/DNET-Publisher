using Microsoft.Ajax.Utilities;
using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DNET_Publisher
{
    class Program
    {
        const string PUBLISHER_FILENAME = ".publisher";
        const int LOG_INDENTATION = 4;
        static string outputDirectory;
        static PublishConfig config;
        static Minifier minifier;
        static string indentation = new string(' ', LOG_INDENTATION);
        static string[] supportedExtensions = new string[] {
            ".css",
            ".js"
        };

        static int Main(string[] args)
        {
            if (args.Length == 0) {
                Console.WriteLine("A directory was expected, but you didn't give me one..");
                Console.ReadKey();
                return 1;
            }

            string dir = args[0];

            if (!Directory.Exists(dir)) {
                Console.WriteLine("Directory does not exist: " + dir);
                Console.ReadKey();
                return 1;
            }

            Directory.SetCurrentDirectory(dir);
            if (!File.Exists(PUBLISHER_FILENAME)) {
                Console.WriteLine($"No {PUBLISHER_FILENAME} file could be found in: " + dir);
                Console.ReadKey();
                return 1;
            }


            Console.WriteLine("Loading publish configuration...");
            config = JsonSerializer.Deserialize<PublishConfig>(File.ReadAllText(PUBLISHER_FILENAME));
            outputDirectory = Path.Combine(dir, config.OutputDir);

            Console.WriteLine("Publishing...");
            using (var process = new Process()) {
                process.StartInfo = new ProcessStartInfo {
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                    FileName = "dotnet",
                    Arguments = $"publish --configuration {config.PublishConfiguration} " +
                                $"--output {outputDirectory.Replace("\"", "\\\"")} " +
                                $"--self-contained {config.SelfContained.ToString().ToLower()} " +
                                $"--runtime {config.PublishRuntime}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                process.Start();
                string result = process.StandardOutput.ReadToEnd();

                process.WaitForExit();
                if (process.ExitCode != 0) {
                    Console.WriteLine("dotnet publish CLI error: " + result);
                    Console.ReadKey();
                    return 1;
                }
            };

            List<string> pendingMinfication = new List<string>();
            foreach (var entry in config.Minify) {
                buildMinification(Path.Combine(dir, outputDirectory, entry.Key), entry.Value, pendingMinfication);
            }

            foreach (var filePath in pendingMinfication) {
                minifyFile(filePath, filePath);
            }

            if (config.Upload != null) {
                using (var sftpClient = getSFTPClient()) {
                    sftpClient.Connect();
                    Console.WriteLine("Cleaning up remote...");
                    deleteFiles(sftpClient, config.Upload.Destination);
                    Console.WriteLine("Publishing content...");
                    uploadFiles(sftpClient, outputDirectory);
                    Console.WriteLine("Cleaning up local...");
                    cleanupOutput(outputDirectory);
                    sftpClient.Disconnect();
                }

                if (config.Upload.Execute == null || config.Upload.Execute.Length > 0) {
                    Console.WriteLine("Executing post publish commands...");
                    using (var sshClient = getSSHClient())
                    {
                        sshClient.Connect();
                        foreach (var commandText in config.Upload.Execute)
                        {
                            var cmd = sshClient.RunCommand(commandText);
                            Console.WriteLine("Executed: " + cmd.CommandText);

                            if (!cmd.Result.IsNullOrWhiteSpace())
                            {
                                Console.WriteLine(cmd.Result);
                            }
                        }
                    }
                }
            }

            Console.WriteLine("Publish complete!");
            Console.WriteLine("Press any key to exit...");
#if RELEASE
            Console.ReadKey();
#endif
            return 0;
        }

        static SftpClient getSFTPClient()
        {
            SftpClient sftpClient;

            if (config.Upload.Key.IsNullOrWhiteSpace()) {
                sftpClient = new SftpClient(config.Upload.getHost(), config.Upload.getPort(), config.Upload.User, config.Upload.Pass);
            }
            else {
                sftpClient = new SftpClient(config.Upload.getHost(), config.Upload.getPort(), config.Upload.User, new PrivateKeyFile(config.Upload.Key));
            }

            return sftpClient;
        }

        static SshClient getSSHClient()
        {
            SshClient sftpClient;

            if (config.Upload.Key.IsNullOrWhiteSpace())
            {
                sftpClient = new SshClient(config.Upload.getHost(), config.Upload.getPort(), config.Upload.User, config.Upload.Pass);
            }
            else
            {
                sftpClient = new SshClient(config.Upload.getHost(), config.Upload.getPort(), config.Upload.User, new PrivateKeyFile(config.Upload.Key));
            }

            return sftpClient;
        }

        static void uploadFiles(SftpClient client, string path)
        {
            foreach (var directoryPath in new DirectoryInfo(path).GetDirectories("*.*", SearchOption.AllDirectories))
            {
                string remoteDir = config.Upload.Destination + directoryPath.FullName.Substring(outputDirectory.Length).Replace('\\', '/');
                Console.WriteLine(indentation + remoteDir);
                client.CreateDirectory(remoteDir);
            }

            foreach (var filePath in new DirectoryInfo(path).GetFiles("*.*", SearchOption.AllDirectories))
            {
                string localDir = filePath.FullName.Substring(outputDirectory.Length);
                string remoteDir = config.Upload.Destination + localDir.Replace('\\', '/');

                if (config.Exclude.Contains(localDir.Substring(1))) {
                    Console.WriteLine("Excluded: " + remoteDir);
                    continue;
                }

                Console.WriteLine(indentation + remoteDir);
                using (var fileStream = File.Open(filePath.FullName, FileMode.Open))
                {
                    client.UploadFile(fileStream, remoteDir);
                    fileStream.Close();
                }
            }
        }

        static void deleteFiles(SftpClient client, string root)
        {
            foreach (var path in client.ListDirectory(root))
            {
                if (path.Name == "." || path.Name == "..")
                    continue;

                if (config.Exclude.Contains(path.FullName.Substring(config.Upload.Destination.Length + 1).Replace('\\', '/')))
                {
                    Console.WriteLine("Excluded: " + path.FullName);
                    continue;
                }


                if (path.IsRegularFile) {
                    Console.WriteLine(indentation + path.FullName);
                    path.Delete();
                }
                else {
                    Console.WriteLine(indentation + path.FullName);
                    deleteFiles(client, path.FullName);
                    path.Delete();
                }
            }
        }

        static void cleanupOutput(string outputDirectory)
        {
            Directory.Delete(outputDirectory, true);
        }

        static void buildMinification(string input, string filter, List<string> outputFiles)
        {
            string[] extensions = filter.Split(';', StringSplitOptions.RemoveEmptyEntries);

            foreach (var extension in extensions)
            {
                string[] files = Directory.GetFiles(input.TrimEnd('*'), extension, input.EndsWith('*') ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

                foreach (var path in files)
                {
                    if (!outputFiles.Contains(path)) {
                        outputFiles.Add(path);
                    }
                }
            }
        }

        static void minifyFile(string input, string output)
        {
            if (minifier == null) {
                minifier = new Minifier();
            }

            string extension = Path.GetExtension(input).ToLower();

            if (!supportedExtensions.Contains(extension))
                return;

            string inputContents = File.ReadAllText(input);
            string outputContents;

            switch (extension)
            {
                case ".css":
                    outputContents = minifier.MinifyStyleSheet(inputContents);
                    break;
                case ".js":
                    outputContents = minifier.MinifyJavaScript(inputContents);
                    break;
                default:
                    return;
            }

            Console.WriteLine("Minified: " + output);
            File.WriteAllText(output, outputContents);
        }
    }
}
