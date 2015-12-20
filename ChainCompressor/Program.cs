using System;
using System.Reflection;
using CommandLine.Text;
using System.IO;
using CommandLine;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace ChainCompressor
{
	static class Program
	{
        private sealed class Options
        {
            [Option('i', "image", Required = true, HelpText = "The input docker image (usually it is a tar file exported by `docker save`).")]
            public string InputImagePath { get; set; }

            [Option('o', "output", Required = true, HelpText = "The output image archive file name (for use in `docker load`).")]
            public string OutputImagePath { get; set; }

            [Option('n', "name", Required = true, HelpText = "The combined image's 'name:version' pair.")]
            public string OutputImageNameVersion { get; set; }

            [HelpOption]
            public string GetUsage()
            {
                var serverAssembly = typeof(Program).Assembly;
                var assemblyName = serverAssembly.GetName();
                var title = serverAssembly.GetCustomAttribute<AssemblyTitleAttribute>();
                var company = serverAssembly.GetCustomAttribute<AssemblyCompanyAttribute>();

                var help = new HelpText {
                    Heading = new HeadingInfo(title.Title, assemblyName.Version.ToString()),
                    Copyright = new CopyrightInfo(company.Company, 2015),
                    AdditionalNewLineAfterOption = true,
                    AddDashesToOption = true
                };
                var assemblyExecutableName = Path.GetFileNameWithoutExtension(serverAssembly.Location);
                help.AddPreOptionsLine(string.Format("Usage: {0} -i image.tar -o image-unpacked", assemblyExecutableName));
                help.AddOptions(this);
                return help;
            }
        }

		public static void Main(string[] args)
		{
            var options = new Options();
            if (CommandLine.Parser.Default.ParseArguments(args, options))
            {
                try
                {
                    Run(options);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Unexpected error happened: ");
                    Console.Error.WriteLine(ex);
                }
            }
            else
            {
                Console.WriteLine(options.GetUsage());
            }
		}

        private static void Run(Options options)
        {
            Match imageNameMatch = Regex.Match(options.OutputImageNameVersion, "^(?<name>.+):(?<version>[^:]+)$");
            if (!imageNameMatch.Success)
            {
                Console.Error.WriteLine(string.Format("Invalid name:version pair '{0}'", options.OutputImageNameVersion));
            }

            string unpackDirectory = Path.Combine(
                Path.GetDirectoryName(options.OutputImagePath),
                Path.GetFileNameWithoutExtension(options.OutputImagePath) + "_temp");
            string inputImageDirectory = Path.Combine(unpackDirectory, "inputImage");
            string combinedLayerDataDirectory = Path.Combine(unpackDirectory, "combinedLayersData");
            string combinedLayerDataPath = Path.Combine(unpackDirectory, "combinedLayer.tar");
            string outputImageDirectory = Path.Combine(unpackDirectory, "outputImage");

            InitializeEmptyDirectory(unpackDirectory);

            Console.Write("Unpacking image {0} into {1} ... ", options.InputImagePath, inputImageDirectory);
            InitializeEmptyDirectory(inputImageDirectory);
            UnpackTarOverwrite(options.InputImagePath, inputImageDirectory);
            Console.WriteLine("Done");

            List<string> layerChain = FindLayersOrder(inputImageDirectory);

            CombineLayers(inputImageDirectory, combinedLayerDataDirectory, layerChain);

            Console.WriteLine("Packing combined layers into single layer...");
            PackTarTo(combinedLayerDataDirectory, combinedLayerDataPath);

            Console.Write("Computing filesystem data size... ");
            long layerSize = Directory
                .EnumerateFiles(combinedLayerDataDirectory, "*", SearchOption.AllDirectories)
                .Select(file => new FileInfo(file).Length)
                .Sum();
            Console.WriteLine("{0} bytes", layerSize);

            Console.WriteLine("Creating layer metadata...");
            var metadata = JObject.Parse(File.ReadAllText(Path.Combine(
                inputImageDirectory, layerChain.Last(), "json")));

            metadata.Property("parent").Remove();
            metadata.Property("container").Remove();
            metadata.Property("Size").Value = layerSize;
            metadata.Property("created").Value = DateTime.UtcNow.ToString("O");

            Console.Write("Generating random image ID... ");
            byte[] bytes = new byte[32];
            new Random().NextBytes(bytes); // random 256-bit ID
            string finalLayerID = string.Concat(bytes.Select(b => b.ToString("X2"))).ToLowerInvariant();
            metadata.Property("id").Value = finalLayerID;
            Console.WriteLine(finalLayerID);

            Console.WriteLine("Gathering layer filesystem data and metadata to finalize image...");
            InitializeEmptyDirectory(outputImageDirectory);

            string finalLayerDirectory = Path.Combine(outputImageDirectory, finalLayerID);
            Directory.CreateDirectory(finalLayerDirectory);
            File.Move(combinedLayerDataPath, Path.Combine(finalLayerDirectory, "layer.tar"));
            File.WriteAllText(Path.Combine(finalLayerDirectory, "json"), metadata.ToString());
            File.WriteAllText(Path.Combine(finalLayerDirectory, "VERSION"), "1.0");

            var imageMetadata = new JObject(
                new JProperty(imageNameMatch.Groups["name"].Value, new JObject(
                    new JProperty(imageNameMatch.Groups["version"].Value, finalLayerID))));
            File.WriteAllText(Path.Combine(outputImageDirectory, "repositories"), imageMetadata.ToString());

            Console.Write("Packing final image... ");
            PackTarTo(outputImageDirectory, options.OutputImagePath);
            Console.WriteLine("Success");

            Console.Write("Cleaning up... ");
            Directory.Delete(unpackDirectory, true);
            Console.WriteLine("Done");
        }

        private static List<string> FindLayersOrder(string inputImageDirectory)
        {
            var repositories = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(
                File.ReadAllText(Path.Combine(inputImageDirectory, "repositories")));

            string layerID = repositories.Single().Value.Single().Value;
            var parentChain = new List<string>();
            do
            {
                parentChain.Add(layerID);
                layerID = JsonConvert.DeserializeAnonymousType(
                    File.ReadAllText(Path.Combine(inputImageDirectory, layerID, "json")),
                    new { parent = "" }).parent;
            }
            while (layerID != null);

            parentChain.Reverse();

            Console.WriteLine("Computed layer chain order:");
            foreach (string layer in parentChain)
            {
                Console.WriteLine("  {0}", layer);
            }

            return parentChain;
        }

        private static void CombineLayers(
            string inputImageDirectory, string combinedLayerDataDirectory, List<string> layerChain)
        {
            InitializeEmptyDirectory(combinedLayerDataDirectory);

            Console.WriteLine("Unpacking layers on top of each other...");
            foreach (string layerID in layerChain)
            {
                Console.Write("Writing layer {0}... ", layerID);
                var layerTarPath = Path.Combine(inputImageDirectory, layerID, "layer.tar");
                UnpackTarOverwrite(layerTarPath, combinedLayerDataDirectory);

                foreach (string file in Directory.EnumerateFiles(
                    combinedLayerDataDirectory, ".wh.*", SearchOption.AllDirectories))
                {
                    File.Delete(file);
                    string unprefixed = file.Replace(".wh.", "");
                    if (File.Exists(unprefixed))
                        File.Delete(unprefixed);
                    else if (Directory.Exists(unprefixed))
                        Directory.Delete(unprefixed, true);
                }

                Console.WriteLine("Done");
            }
        }

        private static void InitializeEmptyDirectory(string directoryPath)
        {
            try { Directory.Delete(directoryPath, true); }
            catch (DirectoryNotFoundException) { }
            Directory.CreateDirectory(directoryPath);
        }

        private static string AddSuffixToDirectory(string directoryPath, string suffix)
        {
            return Path.Combine(
                Directory.GetParent(directoryPath).FullName,
                new DirectoryInfo(directoryPath).Name + suffix);
        }

        private static void UnpackTarOverwrite(string tarFileName, string outputDirectory)
        {
            ShellExecute("/bin/tar", string.Format("-xf \"{0}\" -C \"{1}\"",
                tarFileName, outputDirectory)).Wait();
        }

        private static void PackTarTo(string directoryToPack, string outputTarPath)
        {
            string paths = string.Join(" ",
                from entry in Directory.EnumerateFileSystemEntries(directoryToPack)
                let relative = File.Exists(entry)
                    ? Path.GetFileName(entry) : new DirectoryInfo(entry).Name
                select "\"" + relative + "\"");
            
            ShellExecute("/bin/tar", string.Format("-cf \"{0}\" {1}", outputTarPath, paths),
                workingDirectory: directoryToPack).Wait();
        }

        private static async Task<string> ShellExecute(
            string program, string args,
            TimeSpan? timeout = null,
            string workingDirectory = null)
        {
            ProcessStartInfo processStartInfo = new ProcessStartInfo(program, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Normal,
                UseShellExecute = false,
            };
            
            if (workingDirectory != null)
                processStartInfo.WorkingDirectory = workingDirectory;

            Process process = Process.Start(processStartInfo);

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();

            Task readTask = Task.WhenAll(
                output.ContinueWith(t => process.StandardOutput.Close()),
                error.ContinueWith(t => process.StandardError.Close()));
            
            if (timeout.HasValue)
                await Task.WhenAny(readTask, Task.Delay(timeout.Value));
            else
                await readTask;
           
            if (readTask.IsCompleted)
            {
                return string.Format("{0}{1}{2}", await output, Environment.NewLine, await error);
            }
            else
            {
                try { process.Kill(); }
                catch (Exception) { }
                throw new OperationCanceledException("Shell command execution timed out");
            }
        }
	}
}
