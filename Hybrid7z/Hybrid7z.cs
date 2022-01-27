using System.Collections.Concurrent;

namespace Hybrid7z
{
	public class Hybrid7z
	{
		public const string VERSION = "0.1";
		public const string CONFIG_NAME = "Hybrid7z.ini";

		public readonly Phase[] phases;
		public readonly string currentExecutablePath;

		private readonly Config config;

		// KEY: Target Directory Name (Not Path)
		// VALUE:
		// *** KEY: Phase name
		// *** VALUE: Path of re-builded file list
		public ConcurrentDictionary<string, Dictionary<string, string>> rebuildedFileLists = new();

		public static void Main(string[] args)
		{
			Console.WriteLine($"Hybrid7z v{VERSION}");

			string currentExecutablePath = AppDomain.CurrentDomain.BaseDirectory;

			// Check configuration file is exists
			if (!File.Exists(currentExecutablePath + CONFIG_NAME))
			{
				Console.WriteLine("[CFG] Writing default config");
				Config.SaveDefaults($"{currentExecutablePath}{CONFIG_NAME}");
			}

			// Start the program
			try
			{
				_ = new Hybrid7z(currentExecutablePath, args);
			}
			catch (Exception ex)
			{
				Utils.PrintError("ERR", $"Unhandled exception caught! Please report it to the author: {ex}");
			}
		}

		private void InitializePhases() => Parallel.ForEach(phases, phase =>
										   {
											   Utils.PrintConsoleAndTitle($"[P] Initializing phase: {phase.phaseName}");
											   phase.Init(currentExecutablePath, config);
											   phase.ReadFileList();
										   });

		private static IEnumerable<string> GetTargets(string[] parameters)
		{
			var list = new List<string>();

			foreach (string targetPath in parameters)
				if (Directory.Exists(targetPath))
				{
					string targetPathCopy = targetPath;
					Utils.TrimTrailingPathSeparators(ref targetPathCopy);
					list.Add(targetPathCopy);
				}
				else if (File.Exists(targetPath))
					Console.WriteLine($"[T] WARNING: Currently, file are not supported (only directories are supported) - \"{targetPath}\"");
				else
					Console.WriteLine($"[T] WARNING: File not exists - \"{targetPath}\"");

			return list;
		}

		private void RebuildFileLists(IEnumerable<string> targets)
		{
			foreach (Phase phase in phases)
			{
				string phaseName = phase.phaseName;
				Parallel.ForEach(targets, target =>
				{
					string targetName = Utils.ExtractTargetName(target);
					Console.WriteLine($"[RbFL] Re-building file list for [file=\"{targetName}\", phase={phaseName}]");
					string? path = phase.RebuildFileList(target, $"{targetName}.");

					// Fail-safe
					if (!rebuildedFileLists.ContainsKey(targetName))
						rebuildedFileLists.TryAdd(targetName, new());

					if (rebuildedFileLists.TryGetValue(targetName, out Dictionary<string, string>? map) && path != null)
					{
						// Console.WriteLine($"[RbFL] Re-builded File list for [file=\"{targetName}\", phase={phaseName}] -> {path}");
						map.Add(phaseName, path);
					}
				});
			}
		}

		private bool ProcessPhases(IEnumerable<string> targets)
		{
			bool error = false;

			// Create log file repository
			if (targets.Any())
				Directory.CreateDirectory(currentExecutablePath + "logs");

			var sequentialPhases = new List<Phase>();
			foreach (Phase? phase in phases)
				if (phase.doesntSupportMultiThread)
					error = RunParallelPhase(phase, targets) || error;
				else
					sequentialPhases.Add(phase);

			// Phases with multi-thread support MUST run sequentially, Or they will crash because of insufficient RAM or other system resources. (Especially, LZMA2, Fast-LZMA2 phase)
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

#pragma warning disable CS8618
		public Hybrid7z(string currentExecutablePath, string[] parameters)
#pragma warning restore CS8618
		{
			this.currentExecutablePath = currentExecutablePath;

			Utils.PrintConsoleAndTitle($"[CFG] Loading config... ({CONFIG_NAME})");

			try
			{
				// Load config
				config = new Config(new IniFile($"{currentExecutablePath}{CONFIG_NAME}"));
			}
			catch (Exception ex)
			{
				Utils.PrintError("CFG", $"Can't load config due exception: {ex}");
				return;
			}

			Utils.PrintConsoleAndTitle("[P] Constructing phases...");
			try
			{
				// Construct phases
				phases = new Phase[5];
				phases[0] = new Phase("PPMd", false, true);
				phases[1] = new Phase("Copy", false, true);
				phases[2] = new Phase("LZMA2", false, false);
				phases[3] = new Phase("x86", false, false);
				phases[4] = new Phase("FastLZMA2", true, false);
			}
			catch (Exception ex)
			{
				Utils.PrintError("P", $"Can't construct phases due exception: {ex}");
				return;
			}

			int tick = Environment.TickCount; // For performance-measurement

			// Initialize phases
			Utils.PrintConsoleAndTitle("[P] Initializing phases...");
			InitializePhases();
			Console.WriteLine($"[P] Done initializing phases. (Took {Environment.TickCount - tick}ms)");

			// Filter available targets
			IEnumerable<string>? targets = GetTargets(parameters);

			// Re-build file lists
			Utils.PrintConsoleAndTitle("[RbFL] Re-building file lists...");
			tick = Environment.TickCount;
			try
			{
				RebuildFileLists(targets);
			}
			catch (Exception ex)
			{
				Utils.PrintError("RbFL", $"Exception occurred while re-building filelists: {ex}");
			}

			Console.WriteLine($"[RbFL] Done rebuilding file lists (Took {Environment.TickCount - tick}ms)");
			Console.WriteLine("[C] Now starting the compression...");

			ConsoleColor prevColor = Console.BackgroundColor;
			// Process phases
			if (!targets.Any())
			{
				Console.BackgroundColor = ConsoleColor.DarkYellow;
				Console.WriteLine("[C] No target supplied.");
				Console.BackgroundColor = prevColor;
			}
			else if (ProcessPhases(targets))
			{
				Console.BackgroundColor = ConsoleColor.DarkRed;
				Console.WriteLine("[C] At least one error/warning occurred during the progress.");
				Console.BackgroundColor = prevColor;
			}
			else
			{
				Console.BackgroundColor = ConsoleColor.DarkBlue;
				Console.WriteLine("[C] All files are successfully proceed without any error(s).");
				Console.BackgroundColor = prevColor;
			}
			Console.WriteLine("[DFL] Press any key to delete leftover filelists and exit program...");

			// Wait until any key has been pressed
			Utils.Pause();

			// Delete any left-over filelist files
			foreach (Dictionary<string, string>? files in rebuildedFileLists.Values)
				foreach (string? file in files.Values)
					if (file != null && File.Exists(file))
					{
						File.Delete(file);
						Console.WriteLine($"[DFL] Deleted (re-builded) file list \"{file}\"");
					}
		}

		private bool RunParallelPhase(Phase phase, IEnumerable<string> paths)
		{
			string phaseName = phase.phaseName;
			string prefix = $"PRL-{phaseName}"; //  'P' a 'R' a 'L' lel

			bool includeRoot = config.IncludeRootDirectory;

			// Because Interlocked.CompareExchange doesn't supports bool
			int error = 0;

			Console.WriteLine($"<<==================== Starting Parallel-{phaseName} ====================>>");

			try
			{
				Parallel.ForEach(paths, path =>
				{
					string currentTargetName = Utils.ExtractTargetName(path);
					if (rebuildedFileLists.TryGetValue(currentTargetName, out Dictionary<string, string>? fileListMap) && fileListMap.TryGetValue(phaseName, out string? fileListPath) && fileListPath != null && !phase.PerformPhaseParallel(path, $"-ir@\"{fileListPath}\" -- \"{$"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z"}\""))
						Interlocked.CompareExchange(ref error, 1, 0);
				});
			}
			catch (AggregateException ex)
			{
				Utils.PrintError(prefix, $"AggregateException occurred during parallel execution: {ex}");
				error = 1;
			}

			if (error != 0)
				Utils.PrintError(prefix, $"Error running phase {phaseName} in parallel.");
			else
				Utils.PrintConsoleAndTitle($"[{prefix}] Successfully finished phase {phaseName} without any errors.");
			Console.WriteLine($"<<==================== Finished Parallel-{phaseName} ====================>>");

			return error != 0;
		}

		//private bool RunParallelPhase2(Phase phase, IEnumerable<string> paths)
		//{
		//	string phaseName = phase.phaseName;
		//	string prefix = $"PRL-{phaseName}"; //  'P' a 'R' a 'L' lel

		//	bool includeRoot = config.IncludeRootDirectory;
		//	bool errorDetected = false;

		//	// System.Threading.Tasks.Parallel can't be used in here: Task-list empty check based logics, Console messages should be in ordered
		//	var taskList = new List<Task>(paths.Count());

		//	foreach (string? path in paths)
		//	{
		//		string currentTargetName = Utils.ExtractTargetName(path);
		//		string archiveFileName = $"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z";

		//		if (rebuildedFileLists.TryGetValue(currentTargetName, out Dictionary<string, string>? fileListMap) && fileListMap.TryGetValue(phaseName, out string? fileListPath) && fileListPath != null)
		//		{
		//			Console.WriteLine($"<<==================== Starting Parallel-{phaseName} ====================>>");
		//			Utils.PrintConsoleAndTitle($"[{prefix}] Start processing \"{path}\"");
		//			Console.WriteLine();

		//			Task? task = phase.PerformPhaseParallelAsync(path, $"-ir@\"{fileListPath}\" -- \"{archiveFileName}\"");

		//			if (task != null)
		//				taskList.Add(task);
		//			else
		//			{
		//				errorDetected = true;
		//				Utils.PrintError(prefix, $"Failed to perform parallel phase {phaseName} to \"{path}\"");
		//			}

		//			Console.WriteLine();
		//			Utils.PrintConsoleAndTitle($"[{prefix}] Finished processing {path}");
		//			Console.WriteLine($"<<==================== Finished Parallel-{phaseName} ====================>>");
		//		}
		//	}

		//	if (taskList.Any())
		//	{
		//		Utils.PrintConsoleAndTitle($"[{prefix}] Waiting for all parallel compression processes are finished...");

		//		int tick = Environment.TickCount;
		//		var allTask = Task.WhenAll(taskList);

		//		try
		//		{
		//			allTask.Wait();
		//		}
		//		catch (AggregateException ex)
		//		{
		//			Utils.PrintError(prefix, $"AggregateException occurred during parallel execution: {ex}");
		//		}

		//		Console.WriteLine($"[{prefix}] All parallel compression processes are finished! (Took {Environment.TickCount - tick}ms)");

		//		return errorDetected || allTask.IsFaulted;
		//	}

		//	return errorDetected;
		//}

		private bool RunSequentialPhase(IEnumerable<Phase> phases, string path, string titlePrefix)
		{
			bool includeRoot = config.IncludeRootDirectory;
			string currentTargetName = path[(path.LastIndexOf('\\') + 1)..];
			string archiveFileName = $"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z";

			bool errorOccurred = false;

			Console.WriteLine("<<=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-= <*> =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=>>");

			Utils.PrintConsoleAndTitle($"{titlePrefix} [SQN] Start processing \"{path}\"");

			foreach (Phase? phase in phases)
			{
				if (phase.isTerminal)
				{
					string? list = "";
					if (rebuildedFileLists.TryGetValue(currentTargetName, out Dictionary<string, string>? map))
						list = string.Join(" ", map.Values.ToList().ConvertAll((from) => $"-xr@\"{from}\""));
					errorOccurred = phase.PerformPhaseSequential(path, titlePrefix, $"-r {list} -- \"{archiveFileName}\" \"{(includeRoot ? currentTargetName : "*")}\"") || errorOccurred;
				}
				else if (rebuildedFileLists.TryGetValue(currentTargetName, out Dictionary<string, string>? map) && map.TryGetValue(phase.phaseName, out string? fileListPath) && fileListPath != null)
					errorOccurred = phase.PerformPhaseSequential(path, titlePrefix, $"-ir@\"{fileListPath}\" -- \"{archiveFileName}\"") || errorOccurred;
			}

			Console.WriteLine();
			Utils.PrintConsoleAndTitle($"{titlePrefix} [SQN] Finished processing {path}");

			Console.WriteLine("<<=-=-=-=-=-=-=-=-=-=-=-=-=-=-= <$> <~> =-=-=-=-=-=-=-=-=-=-=-=-=-=-=>>");

			return errorOccurred;
		}
	}
}