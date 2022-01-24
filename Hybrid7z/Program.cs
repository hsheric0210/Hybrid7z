using System.Diagnostics;

namespace Hybrid7z
{
	public class Program
	{
		public const string VERSION = "0.1";

		public readonly Phase[] phases;
		public readonly string currentExecutablePath;
		private readonly Config config;

		public Dictionary<string, Dictionary<string, string>> rebuildedFileListMap = new();

		public bool anyErrors;

		public static void Main(string[] args)
		{
			Console.WriteLine($"Hybrid7z v{VERSION} - A hybrid 7-zip compressor");

			string currentExecutablePath = AppDomain.CurrentDomain.BaseDirectory;

			// Check configuration file is exists
			if (!File.Exists(currentExecutablePath + "Hybrid7z.ini"))
			{
				Console.WriteLine("Writing default config");
				SaveDefaultConfig(currentExecutablePath);
			}

			// Start the program
			new Program(currentExecutablePath, args);
		}

		public Program(string currentExecutablePath, string[] param)
		{
			this.currentExecutablePath = currentExecutablePath;

			config = new Config(new IniFile($"{currentExecutablePath}Hybrid7z.ini"));

			phases = new Phase[5];
			phases[0] = new Phase("PPMd", false, true);
			phases[1] = new Phase("Copy", false, true);
			phases[2] = new Phase("LZMA2", false, false);
			phases[3] = new Phase("x86", false, false);
			phases[4] = new Phase("FastLZMA2", true, false);

			foreach (var phase in phases)
			{
				PrintConsoleAndTitle($"Initializing phase: {phase.phaseName}");
				phase.Init(currentExecutablePath, config);
				phase.ReadFileList();
			}

			int totalFileCount = param.Length;
			var targets = new List<string>();

			foreach (string targetPath in param)
				if (Directory.Exists(targetPath))
					targets.Add(targetPath);
				else if (File.Exists(targetPath))
					Console.WriteLine($"WARNING: Currently, file are not supported (only directories are supported) - \"{targetPath}\"");
				else
					Console.WriteLine($"WARNING: File not exists - \"{targetPath}\"");

			PrintConsoleAndTitle("Re-building file lists...");
			int tick = Environment.TickCount;

			var taskList = new List<Task>();
			foreach (var phase in phases)
			{
				string phaseName = phase.phaseName;
				foreach (string target in targets)
				{
					string targetName = ExtractTargetName(target);
					taskList.Add(phase.RebuildFileList(target, $"{targetName}.").ContinueWith(task =>
					{
						if (!rebuildedFileListMap.ContainsKey(targetName))
							rebuildedFileListMap.Add(targetName, new());

						if (rebuildedFileListMap.TryGetValue(targetName, out Dictionary<string, string>? map) && task.Result != null)
						{
							string path = task.Result;
							Console.WriteLine($"(Re-builded) File list for [file=\"{targetName}\", phase={phaseName}] -> {path}");
							map.Add(phaseName, path);
						}
					}));
				}
			}

			Task.WhenAll(taskList).Wait();

			Console.WriteLine($"Done rebuilding file lists (Took {Environment.TickCount - tick}ms)");

			var multithreadedPhases = new List<Phase>();
			foreach (var phase in phases)
				if (phase.isSingleThreaded)
					anyErrors = RunSinglethreadedPhase(phase, targets) || anyErrors;
				else
					multithreadedPhases.Add(phase);

			int currentFileIndex = 1;
			foreach (var target in targets)
			{
				string titlePrefix = $"[{currentFileIndex}/{totalFileCount}]";
				anyErrors = RunMultithreadedPhase(multithreadedPhases, target, titlePrefix) || anyErrors;
				currentFileIndex++;
			}

			// Print process result
			if (anyErrors)
			{
				Console.WriteLine("One or more file(s) failed to process");
				Console.BackgroundColor = ConsoleColor.DarkRed;
			}
			else
			{
				Console.WriteLine("All files are successfully proceed without any error(s).");
				Console.BackgroundColor = ConsoleColor.DarkBlue;
			}
			Console.WriteLine("Press any key to exit program...");
			Console.ReadKey();

			// Delete any left-over filelist files
			foreach (var files in rebuildedFileListMap.Values)
				foreach (var file in files.Values)
					if (file != null && File.Exists(file))
					{
						File.Delete(file);
						Console.WriteLine($"Deleted {file}");
					}
		}

		private static void SaveDefaultConfig(string currentDir)
		{
			var ini = new IniFile($"{currentDir}Hybrid7z.ini");
			ini.Write("7z", "7z.exe");
			ini.Write("BaseArgs", "a -t7z -mhe -ms=1g -mqs -slp -bt -bb3 -sae");
			ini.Write("Args_PPMd", "-m0=PPMd -mx=9 -myx=9 -mmem=1024m -mo=32 -mmt=1");
			ini.Write("Args_LZMA2", "-m0=LZMA2 -mx=9 -myx=9 -md=256m -mfb=273 -mmt=8 -mmtf -mmf=bt4 -mmc=10000 -mlc=4");
			ini.Write("Args_Copy", "-m0=Copy -mx=0");
			ini.Write("Args_x86", "-mf=BCJ2 -m0=LZMA2 -mx=9 -myx=9 -md=1024m -mfb=273 -mmt=8 -mmtf -mmf=bt4 -mmc=10000 -mlc=4");
			ini.Write("Args_FastLZMA2", "-m0=FLZMA2 -mx=9 -myx=9 -md=1024m -mfb=273 -mmt=32 -mmtf -mlc=4");
			ini.Write("IncludeRootDirectory", "0");
		}

		public static void PrintConsoleAndTitle(string message)
		{
			Console.WriteLine(message);
			Console.Title = message;
		}

		private bool RunSinglethreadedPhase(Phase phase, IEnumerable<string> paths)
		{
			bool includeRoot = config.IncludeRootDirectory;
			bool thereIsNullTask = false;
			var taskList = new List<Task>();

			foreach (var path in paths)
			{
				string currentTargetName = ExtractTargetName(path);
				string archiveFileName = $"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z";

				Console.WriteLine("<<==================== <*> ====================>>");

				if (rebuildedFileListMap.TryGetValue(currentTargetName, out Dictionary<string, string>? fileListMap) && fileListMap.TryGetValue(phase.phaseName, out string? fileListPath) && fileListPath != null)
				{
					PrintConsoleAndTitle($"Start processing \"{path}\"");
					Console.WriteLine();

					var task = phase.PerformPhaseAsync(path, $"-ir@\"{fileListPath}\" -- \"{archiveFileName}\"");

					if (task != null)
						taskList.Add(task);
					else
						thereIsNullTask = true;

					Console.WriteLine();
					PrintConsoleAndTitle($"Finished processing {path}");
				}

				Console.WriteLine("<<==================== <~> <$> ====================>>");
			}

			PrintConsoleAndTitle("Waiting for all asynchronous compression processes are finished...");

			int tick = Environment.TickCount;
			Task allTask = Task.WhenAll(taskList);
			allTask.Wait();

			Console.WriteLine($"All asynchronous compression processes are finished! (Took {Environment.TickCount - tick}ms)");

			return thereIsNullTask || allTask.IsFaulted;
		}

		private bool RunMultithreadedPhase(IEnumerable<Phase> phases, string path, string titlePrefix)
		{
			bool includeRoot = config.IncludeRootDirectory;
			string currentTargetName = path[(path.LastIndexOf('\\') + 1)..];
			string archiveFileName = $"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z";

			bool errorOccurred = false;

			Console.WriteLine("<<=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-= <*> =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=>>");

			PrintConsoleAndTitle($"{titlePrefix} Start processing \"{path}\"");
			Console.WriteLine();

			foreach (var phase in phases)
			{
				if (phase.isTerminal)
				{
					var list = "";
					if (rebuildedFileListMap.TryGetValue(currentTargetName, out Dictionary<string, string>? map))
						list = string.Join(" ", map.Values.ToList().ConvertAll((from) => $"-xr@\"{from}\""));
					errorOccurred = phase.PerformPhase(path, titlePrefix, $"-r {list} -- \"{archiveFileName}\" \"{(includeRoot ? currentTargetName : "*")}\"") || errorOccurred;
				}
				else if (rebuildedFileListMap.TryGetValue(currentTargetName, out Dictionary<string, string>? map) && map.TryGetValue(phase.phaseName, out string? fileListPath) && fileListPath != null)
					errorOccurred = phase.PerformPhase(path, titlePrefix, $"-ir@\"{fileListPath}\" -- \"{archiveFileName}\"") || errorOccurred;
			}

			Console.WriteLine();
			PrintConsoleAndTitle($"{titlePrefix} Finished processing {path}");

			Console.WriteLine("<<=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-= <$> <~> =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=>>");

			return errorOccurred;
		}

		public static void TrimTrailingPathSeparators(ref string path)
		{
			while (path.EndsWith("\\"))
				path = path[(path.LastIndexOf('\\') + 1)..];
		}

		public static string ExtractTargetName(string path)
		{
			TrimTrailingPathSeparators(ref path);
			return path[(path.LastIndexOf('\\') + 1)..];
		}

		public static string ExtractSuperDirectoryName(string path)
		{
			TrimTrailingPathSeparators(ref path);
			return path[..(path.LastIndexOf('\\') + 1)];
		}

	}

	public class Config
	{
		private readonly IniFile config;

		public string SevenZipExecutable;
		public string CommonArguments;
		public bool IncludeRootDirectory;

		public Config(IniFile config)
		{
			this.config = config;

			SevenZipExecutable = config.Read("7z");
			CommonArguments = config.Read("BaseArgs");
			IncludeRootDirectory = !String.Equals(config.Read("IncludeRootDirectory"), "0");
		}

		public string GetPhaseSpecificParameters(string phaseName)
		{
			return config.Read($"Args_{phaseName}");
		}
	}

	public class Phase
	{
		public const string fileListSuffix = ".txt";
		public const string rebuildedFileListSuffix = ".filelist.txt";

		public readonly string phaseName;
		public readonly bool isTerminal;
		public readonly bool isSingleThreaded;

		public string? currentExecutablePath;
		public Config? config;
		public string? phaseParameter;

		public string[]? filterElements;

		public Phase(string phaseName, bool isTerminal, bool isSingleThreaded)
		{
			this.phaseName = phaseName;
			this.isTerminal = isTerminal;
			this.isSingleThreaded = isSingleThreaded;
		}

		public void Init(string currentExecutablePath, Config config)
		{
			this.currentExecutablePath = currentExecutablePath;
			this.config = config;
			phaseParameter = config.GetPhaseSpecificParameters(phaseName);
		}

		public void ReadFileList()
		{
			if (isTerminal)
				return;

			string filelistPath = currentExecutablePath + phaseName + fileListSuffix;
			if (File.Exists(filelistPath))
			{
				Program.PrintConsoleAndTitle($"Reading file list: \"{filelistPath}\"");

				try
				{
					// TODO: Asynchronize
					filterElements = File.ReadAllLines(filelistPath);
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Error reading file list: {ex}");
				}
			}
			else
				Console.WriteLine($"Phase filter file not found for phase: {filelistPath}");
		}

		public Task<string?> RebuildFileList(string path, string fileNamePrefix)
		{
			if (isTerminal || config == null || filterElements == null)
				return Task.FromResult((string?)null);

			bool includeRoot = config.IncludeRootDirectory;
			string targetDirectoryName = Program.ExtractTargetName(path);

			return Task.Run(() =>
			{
				var newFilterElements = new List<string>();
				var tasks = new List<Task>();
				foreach (var filter in filterElements)
					tasks.Add(Task.Run(() =>
					{
						try
						{
							if (Directory.EnumerateFiles(path, filter, SearchOption.AllDirectories).Any())
								newFilterElements.Add((includeRoot ? targetDirectoryName + "\\" : "") + filter);
						}
						catch (Exception ex)
						{
							Console.WriteLine($"Error re-building file list: {ex}");
						}
					}));

				Task.WhenAll(tasks.ToArray()).Wait();

				string? fileListPath = null;

				if (newFilterElements.Any())
				{
					try
					{
						File.WriteAllLines(fileListPath = currentExecutablePath + fileNamePrefix + phaseName + rebuildedFileListSuffix, newFilterElements);
					}
					catch (Exception ex)
					{
						Console.WriteLine($"Error writing re-builded file list: {ex}");
					}
				}

				return fileListPath;
			});
		}

		public Task? PerformPhaseAsync(string path, string extraParameters)
		{
			if (config == null)
				return null;

			string currentTargetName = Program.ExtractTargetName(path);

			Console.WriteLine($">> ===== ----- {phaseName} Phase (Async) ----- ===== <<");
			Program.PrintConsoleAndTitle($"Queued \"{currentTargetName}\" - {phaseName} Phase");
			Console.WriteLine();

			Task? task = null;

			try
			{
				Process sevenzip = new();
				sevenzip.StartInfo.FileName = config.SevenZipExecutable;
				sevenzip.StartInfo.WorkingDirectory = $"{(config.IncludeRootDirectory ? Program.ExtractSuperDirectoryName(path) : path)}\\";
				sevenzip.StartInfo.Arguments = $"{config.CommonArguments} {phaseParameter} {extraParameters}";
				sevenzip.StartInfo.UseShellExecute = true;

				Console.WriteLine("Params: " + sevenzip.StartInfo.Arguments);

				sevenzip.Start();

				task = sevenzip.WaitForExitAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine("Exception while executing 7z asynchronously: " + ex.ToString());
			}

			return task;
		}

		public bool PerformPhase(string path, string indexPrefix, string extraParameters)
		{
			if (config == null)
				return false;

			string currentTargetName = Program.ExtractTargetName(path);

			Console.WriteLine($">> ===== -----<< {phaseName} Phase >>----- ===== <<");
			Program.PrintConsoleAndTitle($"{indexPrefix} Started {indexPrefix} \"{currentTargetName}\" - {phaseName} Phase");
			Console.WriteLine();

			int errorCode = -1;

			try
			{
				Process sevenzip = new();
				sevenzip.StartInfo.FileName = config.SevenZipExecutable;
				sevenzip.StartInfo.WorkingDirectory = $"{(config.IncludeRootDirectory ? Program.ExtractSuperDirectoryName(path) : path)}\\";
				sevenzip.StartInfo.Arguments = $"{config.CommonArguments} {phaseParameter} {extraParameters}";
				sevenzip.StartInfo.UseShellExecute = false;

				Console.WriteLine($"{indexPrefix} Params: {sevenzip.StartInfo.Arguments}");

				sevenzip.Start();
				sevenzip.WaitForExit();
				errorCode = sevenzip.ExitCode;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"{indexPrefix} Exception while executing 7z: {ex}");
			}

			bool error = errorCode != 0;

			if (error)
			{
				Console.Title = $"{indexPrefix} Error compressing \"{path}\" - {phaseName} phase";

				Console.BackgroundColor = ConsoleColor.DarkRed;
				Console.WriteLine($"Compression finished with errors/warnings. (error code {errorCode})"); // TODO: 7-zip error code dictionary are available in online.
				Console.WriteLine("Check the error message and press any key to continue.");
				Console.ReadKey();

				Console.BackgroundColor = ConsoleColor.Black;
			}

			Console.WriteLine();
			Console.WriteLine();
			Program.PrintConsoleAndTitle($"{indexPrefix} \"{currentTargetName}\" - {phaseName} Phase Finished.");

			return error;
		}
	}
}