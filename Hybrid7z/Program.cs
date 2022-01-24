using System.Diagnostics;

namespace Hybrid7z
{
	public class Program
	{
		public const string VERSION = "0.1";
		public const string CONFIG_NAME = "Hybrid7z.ini";

		private static readonly Dictionary<string, string> TargetNameCache = new();
		private static readonly Dictionary<string, string> SuperNameCache = new();

		public readonly Phase[] Phases;
		public readonly string CurrentExecutablePath;

		private readonly Config LocalConfig;

		// KEY: Target Directory Name (Not Path)
		// VALUE:
		// *** KEY: Phase name
		// *** VALUE: Path of re-builded file list
		public Dictionary<string, Dictionary<string, string>> RebuildedFileListMap = new();
		public bool AnyErrorOccurred;

		public static void Main(string[] args)
		{
			Console.WriteLine($"Hybrid7z v{VERSION}");

			string currentExecutablePath = AppDomain.CurrentDomain.BaseDirectory;

			// Check configuration file is exists
			if (!File.Exists(currentExecutablePath + CONFIG_NAME))
			{
				Console.WriteLine("[CFG] Writing default config");
				SaveDefaultConfig(currentExecutablePath);
			}

			// Start the program
			new Program(currentExecutablePath, args);
		}

		public Program(string currentExecutablePath, string[] param)
		{
			this.CurrentExecutablePath = currentExecutablePath;

			PrintConsoleAndTitle($"[CFG] Loading config... ({CONFIG_NAME})");
			LocalConfig = new Config(new IniFile($"{currentExecutablePath}{CONFIG_NAME}"));

			Phases = new Phase[5];
			Phases[0] = new Phase("PPMd", false, true);
			Phases[1] = new Phase("Copy", false, true);
			Phases[2] = new Phase("LZMA2", false, false);
			Phases[3] = new Phase("x86", false, false);
			Phases[4] = new Phase("FastLZMA2", true, false);

			var taskList = new List<Task>();

			PrintConsoleAndTitle("[P] Initializing phases...");
			int tick = Environment.TickCount;

			foreach (var phase in Phases)
			{
				PrintConsoleAndTitle($"[P] Initializing phase: {phase.phaseName}");
				phase.Init(currentExecutablePath, LocalConfig);

				var task = phase.ReadFileList();
				if (task != null)
					taskList.Add(task);
			}

			Task.WhenAll(taskList).Wait();
			Console.WriteLine($"[P] Done initializing phases. (Took {Environment.TickCount - tick}ms)");

			taskList.Clear();

			int totalFileCount = param.Length;
			var targets = new List<string>();

			foreach (string targetPath in param)
				if (Directory.Exists(targetPath))
					targets.Add(targetPath);
				else if (File.Exists(targetPath))
					Console.WriteLine($"[T] WARNING: Currently, file are not supported (only directories are supported) - \"{targetPath}\"");
				else
					Console.WriteLine($"[T] WARNING: File not exists - \"{targetPath}\"");

			PrintConsoleAndTitle("[RbFL] Re-building file lists...");
			tick = Environment.TickCount;

			foreach (var phase in Phases)
			{
				string phaseName = phase.phaseName;
				foreach (string target in targets)
				{
					string targetName = ExtractTargetName(target);
					taskList.Add(phase.RebuildFileList(target, $"{targetName}.").ContinueWith(task =>
					{
						if (!RebuildedFileListMap.ContainsKey(targetName))
							RebuildedFileListMap.Add(targetName, new());

						if (RebuildedFileListMap.TryGetValue(targetName, out Dictionary<string, string>? map) && task.Result != null)
						{
							string path = task.Result;
							Console.WriteLine($"[RbFL] (Re-builded) File list for [file=\"{targetName}\", phase={phaseName}] -> {path}");
							map.Add(phaseName, path);
						}
					}));
				}
			}

			Task.WhenAll(taskList).Wait();

			Console.WriteLine($"[RbFL] Done rebuilding file lists (Took {Environment.TickCount - tick}ms)");
			Console.WriteLine("[C] Now starting the compression...");

			var sequentialPhases = new List<Phase>();
			foreach (var phase in Phases)
				if (phase.doesntSupportMultiThread)
					AnyErrorOccurred = RunParallelPhase(phase, targets) || AnyErrorOccurred;
				else
					sequentialPhases.Add(phase);

			// Phases with multi-thread support MUST run sequentially, Or they will crash because of insufficient RAM or other system resources.
			int currentFileIndex = 1;
			foreach (var target in targets)
			{
				string titlePrefix = $"[{currentFileIndex}/{totalFileCount}]";
				AnyErrorOccurred = RunSequentialPhase(sequentialPhases, target, titlePrefix) || AnyErrorOccurred;
				currentFileIndex++;
			}

			// Print process result
			if (AnyErrorOccurred)
			{
				Console.WriteLine("[C] One or more file(s) failed to compress");
				Console.BackgroundColor = ConsoleColor.DarkRed;
			}
			else
			{
				Console.WriteLine("[C] All files are successfully proceed without any error(s).");
				Console.BackgroundColor = ConsoleColor.DarkBlue;
			}
			Console.WriteLine("[DFL] Press any key to delete leftover filelists and exit program...");

			ConsoleKeyInfo ci;
			do
			{
				ci = Console.ReadKey();
			} while (ci.Modifiers != 0);

			// Delete any left-over filelist files
			foreach (var files in RebuildedFileListMap.Values)
				foreach (var file in files.Values)
					if (file != null && File.Exists(file))
					{
						File.Delete(file);
						Console.WriteLine($"[DFL] Deleted (re-builded) file list \"{file}\"");
					}
		}

		private static void SaveDefaultConfig(string currentDir)
		{
			var ini = new IniFile($"{currentDir}{CONFIG_NAME}");
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

		public static void TrimTrailingPathSeparators(ref string path)
		{
			while (path.EndsWith("\\"))
				path = path[(path.LastIndexOf('\\') + 1)..];
		}

		public static string ExtractTargetName(string path)
		{
			if (TargetNameCache.TryGetValue(path, out var targetName))
				return targetName;

			TrimTrailingPathSeparators(ref path);

			targetName = path[(path.LastIndexOf('\\') + 1)..];
			TargetNameCache.Add(path, targetName);
			return targetName;
		}

		public static string ExtractSuperDirectoryName(string path)
		{
			if (SuperNameCache.TryGetValue(path, out var superName))
				return superName;

			TrimTrailingPathSeparators(ref path);

			superName = path[..(path.LastIndexOf('\\') + 1)];
			SuperNameCache.Add(path, superName);
			return superName;
		}

		private bool RunParallelPhase(Phase phase, IEnumerable<string> paths)
		{
			string phaseName = phase.phaseName;

			bool includeRoot = LocalConfig.IncludeRootDirectory;
			bool thereIsNullTask = false;
			var taskList = new List<Task>();

			foreach (var path in paths)
			{
				string currentTargetName = ExtractTargetName(path);
				string archiveFileName = $"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z";

				Console.WriteLine("<<==================== <*> ====================>>");

				if (RebuildedFileListMap.TryGetValue(currentTargetName, out Dictionary<string, string>? fileListMap) && fileListMap.TryGetValue(phaseName, out string? fileListPath) && fileListPath != null)
				{
					PrintConsoleAndTitle($"[PRL-{phaseName}] Start processing \"{path}\"");
					Console.WriteLine();

					var task = phase.PerformPhaseParallel(path, $"-ir@\"{fileListPath}\" -- \"{archiveFileName}\"");

					if (task != null)
						taskList.Add(task);
					else
						thereIsNullTask = true;

					Console.WriteLine();
					PrintConsoleAndTitle($"[PRL-{phaseName}] Finished processing {path}");
				}

				Console.WriteLine("<<==================== <~> <$> ====================>>");
			}

			PrintConsoleAndTitle($"[PRL-{phaseName}] Waiting for all parallel compression processes are finished...");

			int tick = Environment.TickCount;
			Task allTask = Task.WhenAll(taskList);
			allTask.Wait();

			Console.WriteLine($"[PRL-{phaseName}] All parallel compression processes are finished! (Took {Environment.TickCount - tick}ms)");

			return thereIsNullTask || allTask.IsFaulted;
		}

		private bool RunSequentialPhase(IEnumerable<Phase> phases, string path, string titlePrefix)
		{
			bool includeRoot = LocalConfig.IncludeRootDirectory;
			string currentTargetName = path[(path.LastIndexOf('\\') + 1)..];
			string archiveFileName = $"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z";

			bool errorOccurred = false;

			Console.WriteLine("<<=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-= <*> =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=>>");

			PrintConsoleAndTitle($"{titlePrefix} [SQN] Start processing \"{path}\"");
			Console.WriteLine();

			foreach (var phase in phases)
			{
				if (phase.isTerminal)
				{
					var list = "";
					if (RebuildedFileListMap.TryGetValue(currentTargetName, out Dictionary<string, string>? map))
						list = string.Join(" ", map.Values.ToList().ConvertAll((from) => $"-xr@\"{from}\""));
					errorOccurred = phase.PerformPhaseSequential(path, titlePrefix, $"-r {list} -- \"{archiveFileName}\" \"{(includeRoot ? currentTargetName : "*")}\"") || errorOccurred;
				}
				else if (RebuildedFileListMap.TryGetValue(currentTargetName, out Dictionary<string, string>? map) && map.TryGetValue(phase.phaseName, out string? fileListPath) && fileListPath != null)
					errorOccurred = phase.PerformPhaseSequential(path, titlePrefix, $"-ir@\"{fileListPath}\" -- \"{archiveFileName}\"") || errorOccurred;
			}

			Console.WriteLine();
			PrintConsoleAndTitle($"{titlePrefix} [SQN] Finished processing {path}");

			Console.WriteLine("<<=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-= <$> <~> =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=>>");

			return errorOccurred;
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
		public readonly bool doesntSupportMultiThread;

		public string? currentExecutablePath;
		public Config? config;
		public string? phaseParameter;

		public string[]? filterElements;

		public Phase(string phaseName, bool isTerminal, bool doesntSupportMultiThread)
		{
			this.phaseName = phaseName;
			this.isTerminal = isTerminal;
			this.doesntSupportMultiThread = doesntSupportMultiThread;
		}

		public void Init(string currentExecutablePath, Config config)
		{
			this.currentExecutablePath = currentExecutablePath;
			this.config = config;
			phaseParameter = config.GetPhaseSpecificParameters(phaseName);
		}

		public Task? ReadFileList()
		{
			if (isTerminal)
				return null;

			string filelistPath = currentExecutablePath + phaseName + fileListSuffix;
			if (File.Exists(filelistPath))
			{
				Program.PrintConsoleAndTitle($"[RFL] Reading file list: \"{filelistPath}\"");

				try
				{
					return File.ReadAllLinesAsync(filelistPath).ContinueWith(task =>
					{
						var strings = new List<string>();
						foreach (string line in task.Result)
						{
							string commentRemoved = line.Contains("//") ? line[..line.IndexOf("//")] : line;
							if (!string.IsNullOrWhiteSpace(commentRemoved))
							{
								Console.WriteLine($"[RFL] Readed from {filelistPath}: \"{commentRemoved}\"");
								strings.Add(commentRemoved.Trim());
							}
						}
						filterElements = strings.ToArray();
					});
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[RFL] Error reading file list: {ex}");
				}
			}
			else
				Console.WriteLine($"[RFL] Phase filter file not found for phase: {filelistPath}");

			return null;
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
							{
								Console.WriteLine($"[RbFL] Found files for filter \"{filter}\"");
								newFilterElements.Add((includeRoot ? targetDirectoryName + "\\" : "") + filter);
							}
						}
						catch (Exception ex)
						{
							Console.WriteLine($"[RbFL] Error re-building file list: {ex}");
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
						Console.WriteLine($"[RbFL] Error writing re-builded file list: {ex}");
					}
				}

				return fileListPath;
			});
		}

		public Task? PerformPhaseParallel(string path, string extraParameters)
		{
			if (config == null)
				return null;

			string currentTargetName = Program.ExtractTargetName(path);

			Console.WriteLine($">> ===== ----- {phaseName} Phase (Parallel) ----- ===== <<");
			Program.PrintConsoleAndTitle($"[PRL-{phaseName}] Queued \"{currentTargetName}\" - {phaseName} Phase");
			Console.WriteLine();

			Task? task = null;

			try
			{
				Process sevenzip = new();
				sevenzip.StartInfo.FileName = config.SevenZipExecutable;
				sevenzip.StartInfo.WorkingDirectory = $"{(config.IncludeRootDirectory ? Program.ExtractSuperDirectoryName(path) : path)}\\";
				sevenzip.StartInfo.Arguments = $"{config.CommonArguments} {phaseParameter} {extraParameters}";
				sevenzip.StartInfo.UseShellExecute = true;

				Console.WriteLine($"[PRL-{phaseName}] Params: {sevenzip.StartInfo.Arguments}");

				sevenzip.Start();

				task = sevenzip.WaitForExitAsync().ContinueWith((task) =>
				{
					int errorCode = sevenzip.ExitCode;
					if (errorCode != 0)
						Console.WriteLine($"[PRL-{phaseName}] Compression finished with errors/warnings. Error code {errorCode} ({Get7ZipExitCodeInformation(errorCode)})");
				});
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[PRL-{phaseName}] Exception while executing 7z in parallel: {ex}");
			}

			return task;
		}

		public bool PerformPhaseSequential(string path, string indexPrefix, string extraParameters)
		{
			if (config == null)
				return false;

			string currentTargetName = Program.ExtractTargetName(path);

			Console.WriteLine($">> ===== -----<< {phaseName} Phase (Sequential) >>----- ===== <<");
			Program.PrintConsoleAndTitle($"{indexPrefix} [SQN] Started {indexPrefix} \"{currentTargetName}\" - {phaseName} Phase");
			Console.WriteLine();

			int errorCode = -1;

			try
			{
				Process sevenzip = new();
				sevenzip.StartInfo.FileName = config.SevenZipExecutable;
				sevenzip.StartInfo.WorkingDirectory = $"{(config.IncludeRootDirectory ? Program.ExtractSuperDirectoryName(path) : path)}\\";
				sevenzip.StartInfo.Arguments = $"{config.CommonArguments} {phaseParameter} {extraParameters}";
				sevenzip.StartInfo.UseShellExecute = false;

				Console.WriteLine($"{indexPrefix} [SQN] Params: {sevenzip.StartInfo.Arguments}");

				sevenzip.Start();
				sevenzip.WaitForExit();
				errorCode = sevenzip.ExitCode;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"{indexPrefix} [SQN] Exception while executing 7z: {ex}");
			}

			bool error = errorCode != 0;

			if (error)
			{
				Console.Title = $"{indexPrefix} [SQN] Error compressing \"{path}\" - {phaseName} phase";

				Console.BackgroundColor = ConsoleColor.DarkRed;
				Console.WriteLine($"[SQN] Compression finished with errors/warnings. Error code {errorCode} ({Get7ZipExitCodeInformation(errorCode)})");
				Console.WriteLine("[SQN] Check the error message and press any key to continue.");
				Console.ReadKey();

				Console.BackgroundColor = ConsoleColor.Black;
			}

			Console.WriteLine();
			Console.WriteLine();
			Program.PrintConsoleAndTitle($"{indexPrefix} [SQN] \"{currentTargetName}\" - {phaseName} Phase Finished.");

			return error;
		}

		private static string Get7ZipExitCodeInformation(int exitCode)
		{
			return exitCode switch
			{
				1 => "Non-fatal warning(s)",
				2 => "Fatal error",
				7 => "Command-line error",
				8 => "Not enough memory for operation",
				255 => "User stopped the process",
				_ => "",
			};
		}
	}
}