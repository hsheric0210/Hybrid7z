namespace Hybrid7z
{
	public class Hybrid7z
	{
		public const string VERSION = "0.1";
		public const string CONFIG_NAME = "Hybrid7z.ini";

		public readonly Phase[] Phases;
		public readonly string CurrentExecutablePath;

		private readonly Config LocalConfig;

		// KEY: Target Directory Name (Not Path)
		// VALUE:
		// *** KEY: Phase name
		// *** VALUE: Path of re-builded file list
		public Dictionary<string, Dictionary<string, string>> RebuildedFileListMap = new();

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
			new Hybrid7z(currentExecutablePath, args);
		}

		private void InitializePhases(List<Task> taskList)
		{
			foreach (Phase? phase in Phases)
			{
				Utils.PrintConsoleAndTitle($"[P] Initializing phase: {phase.phaseName}");
				phase.Init(CurrentExecutablePath, LocalConfig);

				Task? task = phase.ReadFileList();
				if (task != null)
					taskList.Add(task);
			}
		}

		private static IEnumerable<string> GetTargets(string[] parameters)
		{
			var list = new List<string>();

			foreach (string targetPath in parameters)
				if (Directory.Exists(targetPath))
					list.Add(targetPath);
				else if (File.Exists(targetPath))
					Console.WriteLine($"[T] WARNING: Currently, file are not supported (only directories are supported) - \"{targetPath}\"");
				else
					Console.WriteLine($"[T] WARNING: File not exists - \"{targetPath}\"");

			return list;
		}

		private void RebuildFileLists(IEnumerable<string> targets, List<Task> taskList)
		{
			foreach (Phase phase in Phases)
			{
				string phaseName = phase.phaseName;
				foreach (string target in targets)
				{
					string targetName = Utils.ExtractTargetName(target);
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
		}

		private bool ProcessPhases(IEnumerable<string> targets)
		{
			bool error = false;

			var sequentialPhases = new List<Phase>();
			foreach (Phase? phase in Phases)
				if (phase.doesntSupportMultiThread)
					error = RunParallelPhase(phase, targets) || error;
				else
					sequentialPhases.Add(phase);

			// Phases with multi-thread support MUST run sequentially, Or they will crash because of insufficient RAM or other system resources.
			int totalTargetCount = targets.Count();
			int currentFileIndex = 1;
			foreach (string? target in targets)
			{
				string titlePrefix = $"[{currentFileIndex}/{totalTargetCount}]";
				error = RunSequentialPhase(sequentialPhases, target, titlePrefix) || error;
				currentFileIndex++;
			}

			return error;
		}

		public Hybrid7z(string currentExecutablePath, string[] parameters)
		{
			CurrentExecutablePath = currentExecutablePath;

			Utils.PrintConsoleAndTitle($"[CFG] Loading config... ({CONFIG_NAME})");
			LocalConfig = new Config(new IniFile($"{currentExecutablePath}{CONFIG_NAME}"));

			// Construct phases
			Phases = new Phase[5];
			Phases[0] = new Phase("PPMd", false, true);
			Phases[1] = new Phase("Copy", false, true);
			Phases[2] = new Phase("LZMA2", false, false);
			Phases[3] = new Phase("x86", false, false);
			Phases[4] = new Phase("FastLZMA2", true, false);

			int tick = Environment.TickCount; // For performance-measurement

			// Initialize phases
			Utils.PrintConsoleAndTitle("[P] Initializing phases...");
			var taskList = new List<Task>();
			InitializePhases(taskList);
			Task.WhenAll(taskList).Wait();
			Console.WriteLine($"[P] Done initializing phases. (Took {Environment.TickCount - tick}ms)");

			taskList.Clear(); // Re-use task list

			// Filter available targets
			IEnumerable<string>? targets = GetTargets(parameters);
			int totalTargetCount = targets.Count();

			// Re-build file lists
			Utils.PrintConsoleAndTitle("[RbFL] Re-building file lists...");
			tick = Environment.TickCount;
			RebuildFileLists(targets, taskList);
			Task.WhenAll(taskList).Wait();

			Console.WriteLine($"[RbFL] Done rebuilding file lists (Took {Environment.TickCount - tick}ms)");
			Console.WriteLine("[C] Now starting the compression...");

			// Process phases
			if (ProcessPhases(targets))
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

			// Wait until any key has been pressed
			ConsoleKeyInfo ci;
			do

				ci = Console.ReadKey();
			while (ci.Modifiers != 0);

			// Delete any left-over filelist files
			foreach (Dictionary<string, string>? files in RebuildedFileListMap.Values)
				foreach (string? file in files.Values)
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

		private bool RunParallelPhase(Phase phase, IEnumerable<string> paths)
		{
			string phaseName = phase.phaseName;

			bool includeRoot = LocalConfig.IncludeRootDirectory;
			bool thereIsNullTask = false;
			var taskList = new List<Task>();

			foreach (string? path in paths)
			{
				string currentTargetName = Utils.ExtractTargetName(path);
				string archiveFileName = $"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z";

				Console.WriteLine("<<==================== <*> ====================>>");

				if (RebuildedFileListMap.TryGetValue(currentTargetName, out Dictionary<string, string>? fileListMap) && fileListMap.TryGetValue(phaseName, out string? fileListPath) && fileListPath != null)
				{
					Utils.PrintConsoleAndTitle($"[PRL-{phaseName}] Start processing \"{path}\"");
					Console.WriteLine();

					Task? task = phase.PerformPhaseParallel(path, $"-ir@\"{fileListPath}\" -- \"{archiveFileName}\"");

					if (task != null)
						taskList.Add(task);
					else
						thereIsNullTask = true;

					Console.WriteLine();
					Utils.PrintConsoleAndTitle($"[PRL-{phaseName}] Finished processing {path}");
				}

				Console.WriteLine("<<==================== <~> <$> ====================>>");
			}

			Utils.PrintConsoleAndTitle($"[PRL-{phaseName}] Waiting for all parallel compression processes are finished...");

			int tick = Environment.TickCount;
			var allTask = Task.WhenAll(taskList);
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

			Utils.PrintConsoleAndTitle($"{titlePrefix} [SQN] Start processing \"{path}\"");
			Console.WriteLine();

			foreach (Phase? phase in phases)
			{
				if (phase.isTerminal)
				{
					string? list = "";
					if (RebuildedFileListMap.TryGetValue(currentTargetName, out Dictionary<string, string>? map))
						list = string.Join(" ", map.Values.ToList().ConvertAll((from) => $"-xr@\"{from}\""));
					errorOccurred = phase.PerformPhaseSequential(path, titlePrefix, $"-r {list} -- \"{archiveFileName}\" \"{(includeRoot ? currentTargetName : "*")}\"") || errorOccurred;
				}
				else if (RebuildedFileListMap.TryGetValue(currentTargetName, out Dictionary<string, string>? map) && map.TryGetValue(phase.phaseName, out string? fileListPath) && fileListPath != null)
					errorOccurred = phase.PerformPhaseSequential(path, titlePrefix, $"-ir@\"{fileListPath}\" -- \"{archiveFileName}\"") || errorOccurred;
			}

			Console.WriteLine();
			Utils.PrintConsoleAndTitle($"{titlePrefix} [SQN] Finished processing {path}");

			Console.WriteLine("<<=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-= <$> <~> =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=>>");

			return errorOccurred;
		}
	}
}