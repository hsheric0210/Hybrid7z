using System.Collections.Concurrent;
using System.Text;

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
		public HashSet<string> terminalPhaseInclusion = new();

		public EnumerationOptions recursiveEnumeratorOptions = new()
		{
			AttributesToSkip = 0,
			RecurseSubdirectories = true
		};

		public static void Main(string[] args)
		{
			Console.WriteLine($"Hybrid7z v{VERSION}");

			string currentExecutablePath = AppDomain.CurrentDomain.BaseDirectory;

			// Check configuration file is exists
			if (!File.Exists(currentExecutablePath + CONFIG_NAME))
			{
				Console.WriteLine("[CFG] Writing default config");
				Config.saveDefaults($"{currentExecutablePath}{CONFIG_NAME}");
			}

			// Start the program
			try
			{
				_ = new Hybrid7z(currentExecutablePath, args);
			}
			catch (Exception ex)
			{
				Utils.printError("ERR", $"Unhandled exception caught! Please report it to the author: {ex}");
			}
		}

		private void initPhases(string logDir) => Parallel.ForEach(phases, phase =>
										   {
											   Utils.printConsoleAndTitle($"[P] Initializing phase: {phase.phaseName}");
											   phase.init(currentExecutablePath, config, logDir);
											   phase.readFileList();
										   });

		private static IEnumerable<string> getTargets(string[] parameters, int switchIndex)
		{
			var list = new List<string>();

			for (int i = switchIndex; i < parameters.Length; i++)
			{
				string targetPath = parameters[i];
				if (Directory.Exists(targetPath))
				{
					string targetPathCopy = targetPath;
					Utils.trimTrailingPathSeparators(ref targetPathCopy);
					list.Add(targetPathCopy);
				}
				else if (File.Exists(targetPath))
					Console.WriteLine($"[T] WARNING: Currently, file are not supported (only directories are supported) - \"{targetPath}\"");
				else
					Console.WriteLine($"[T] WARNING: File not exists - \"{targetPath}\"");
			}

			return list;
		}

		private void rebuildFileLists(IEnumerable<string> targets)
		{
			// KEY: target
			// VALUE: Set of available files
			ConcurrentDictionary<string, HashSet<string>> availableFiles = new();

			Parallel.ForEach(targets, target =>
			{
				availableFiles[target] = Directory.EnumerateFiles(target, "*", recursiveEnumeratorOptions).Select(path => path.ToUpperInvariant()).ToHashSet();
			});

			foreach (Phase phase in phases)
			{
				string phaseName = phase.phaseName;
				Parallel.ForEach(targets, target =>
				{
					string targetName = Utils.extractTargetName(target);
					Console.WriteLine($"[RbFL] Re-building file list for [file=\"{targetName}\", phase={phaseName}]");

					HashSet<string>? availableFilesForCurrentTarget = availableFiles[target];

					// This will remove all paths(files) matching the specified filter from availableFilesForThisTarget.
					string? path = phase.rebuildFileList(target, $"{targetName}.", recursiveEnumeratorOptions, ref availableFilesForCurrentTarget);

					// Fail-safe
					if (!rebuildedFileLists.ContainsKey(targetName))
						rebuildedFileLists[targetName] = new();

					Dictionary<string, string> map = rebuildedFileLists[targetName];
					if (path != null)
						map.Add(phaseName, path);
				});
			}

			// TODO: Improve this ugly solution
			foreach (string target in targets)
				if (availableFiles.TryGetValue(target, out HashSet<string>? avail) && avail.Count > 0)
					terminalPhaseInclusion.Add(target);
		}

		private bool processPhase(IEnumerable<string> targets)
		{
			bool error = false;

			// Create log file repository
			if (targets.Any())
				Directory.CreateDirectory($"{currentExecutablePath}logs");

			string extraParameters = "";
			if (File.Exists("Exclude.txt"))
				extraParameters = $"-xr@\"{currentExecutablePath}Exclude.txt\"";

			var sequentialPhases = new List<Phase>();
			foreach (Phase? phase in phases)
				if (phase.doesntSupportMultiThread)
					error = runParallelPhase(phase, targets, extraParameters) || error;
				else
					sequentialPhases.Add(phase);

			// Phases with multi-thread support MUST run sequentially, Or they will crash because of insufficient RAM or other system resources. (Especially, LZMA2, Fast-LZMA2 phase)
			int totalTargetCount = targets.Count();
			int currentFileIndex = 1;
			foreach (string? target in targets)
			{
				string titlePrefix = $"[{currentFileIndex}/{totalTargetCount}]";
				error = runSequentialPhases(sequentialPhases, target, titlePrefix, extraParameters) || error;
				currentFileIndex++;
			}

			long original = 0;
			long compressed = 0;
			foreach (string? target in targets)
			{
				(long original, long compressed) currentRatio = Utils.getCompressionRatio(target, recursiveEnumeratorOptions);
				original += currentRatio.original;
				compressed += currentRatio.compressed;
			}

			Utils.printCompressionRatio("(Overall)", original, compressed);

			return error;
		}

#pragma warning disable CS8618
		public Hybrid7z(string currentExecutablePath, string[] parameters)
#pragma warning restore CS8618
		{
			this.currentExecutablePath = currentExecutablePath;

			string logRoot = "logs";

			// Parse the command-line parameters
			int switchIndex = 0;
			if (parameters.Length > 0)
			{
				for (int n = parameters.Length; switchIndex < n; switchIndex++)
				{
					string rawParam = parameters[switchIndex];
					if (rawParam.StartsWith('/') || rawParam.StartsWith('-'))
					{
						// Trim all leading '/' and '-' characters
						string realParam;
						do
							realParam = rawParam[1..];
						while (realParam.StartsWith('/') || realParam.StartsWith('-'));

						switch (realParam.ToLowerInvariant())
						{
							case "help":
							case "?":
							case "h":
							{
								Console.WriteLine("Hybrid7z.exe <switches> <target folders>");
								Console.WriteLine("Note that 'Hybrid7z.exe <target folders> <switches>' is not working");
								Console.WriteLine("Available commands:");
								Console.WriteLine("\t/help, /h, /?\t\t\t\t\tPrint the usages");
								Console.WriteLine("\t/log, /logDir, /logRoot\t\t<dirName>\tSpecify the directory to save the log files (non-existent directory name is allowed)");
								return;
							}

							case "log":
							case "logdir":
							case "logroot":
							{
								if (parameters.Length > switchIndex + 1)
									logRoot = parameters[switchIndex + 1];
								else
									Console.WriteLine("[LogRoot] You must specify the log root folder after the logRoot switch (ex: '-logroot logs')");
								switchIndex++; // Skip trailing directory name part

								break;
							}
						}
					}
					else
						break;
				}
			}

			Utils.printConsoleAndTitle($"[CFG] Loading config... ({CONFIG_NAME})");

			try
			{
				// Load config
				config = new Config(new IniFile($"{currentExecutablePath}{CONFIG_NAME}"));
			}
			catch (Exception ex)
			{
				Utils.printError("CFG", $"Can't load config due exception: {ex}");
				return;
			}

			Utils.printConsoleAndTitle("[P] Constructing phases...");
			try
			{
				// Construct phases
				string[] phaseNames = config.phases;
				int count = phaseNames.Length;
				phases = new Phase[count];
				for (int i = 0; i < count; i++)
				{
					string phaseName = phaseNames[i][1..];
					bool terminal = i >= count - 1;
					bool runParallel = char.ToLower(phaseNames[i][0]) == 'y';
					phases[i] = new Phase(phaseName, terminal, runParallel);
					Console.WriteLine($"[P] Phase[{i}] = \"{phaseName}\" (terminal={terminal}, parallel={runParallel})");
				}
			}
			catch (Exception ex)
			{
				Utils.printError("P", $"Can't construct phases due exception: {ex}");
				return;
			}

			int tick = Environment.TickCount; // For performance-measurement

			// Initialize phases
			Utils.printConsoleAndTitle("[P] Initializing phases...");
			initPhases(logRoot);
			Console.WriteLine($"[P] Done initializing phases. (Took {Environment.TickCount - tick}ms)");

			// Filter available targets
			IEnumerable<string>? targets = getTargets(parameters, switchIndex);

			// Re-build file lists
			Utils.printConsoleAndTitle("[RbFL] Re-building file lists...");
			tick = Environment.TickCount;
			try
			{
				rebuildFileLists(targets);
			}
			catch (Exception ex)
			{
				Utils.printError("RbFL", $"Exception occurred while re-building filelists: {ex}");
			}

			Console.WriteLine($"[RbFL] Done rebuilding file lists (Took {Environment.TickCount - tick}ms)");
			Console.WriteLine("[C] Now starting the compression...");

			ConsoleColor prevColor = Console.ForegroundColor;

			// Process phases
			if (!targets.Any())
			{
				Console.ForegroundColor = ConsoleColor.DarkYellow;
				Console.WriteLine("[C] No target supplied.");
				Console.ForegroundColor = prevColor;
			}
			else if (processPhase(targets))
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("[C] At least one error/warning occurred during the progress.");
				Console.ForegroundColor = prevColor;
			}
			else
			{
				Console.ForegroundColor = ConsoleColor.Blue;
				Console.WriteLine("[C] All files are successfully proceed without any error(s).");
				Console.ForegroundColor = prevColor;
			}
			Console.WriteLine("[DFL] Press any key and enter to delete leftover filelists...");

			// Wait until any key has been pressed
			Utils.pause();

			// Delete any left-over filelist files
			foreach (Dictionary<string, string>? files in rebuildedFileLists.Values)
				foreach (string? file in files.Values)
					if (file != null && File.Exists(file))
					{
						File.Delete(file);
						Console.WriteLine($"[DFL] Deleted (re-builded) file list \"{file}\"");
					}

			Console.WriteLine("Press any key and enter to exit program...");

			// Wait until any key has been pressed
			Utils.pause();
		}

		private bool runParallelPhase(Phase phase, IEnumerable<string> paths, string extraParameters)
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
					string currentTargetName = Utils.extractTargetName(path);
					if (rebuildedFileLists.TryGetValue(currentTargetName, out Dictionary<string, string>? fileListMap) && fileListMap.TryGetValue(phaseName, out string? fileListPath) && fileListPath != null && !phase.performPhaseParallel(path, $"{extraParameters} -ir@\"{fileListPath}\" -- \"{$"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z"}\""))
						Interlocked.CompareExchange(ref error, 1, 0);
				});
			}
			catch (AggregateException ex)
			{
				Utils.printError(prefix, $"AggregateException occurred during parallel execution: {ex}");
				error = 1;
			}

			if (error != 0)
				Utils.printError(prefix, $"Error running phase {phaseName} in parallel.");
			else
				Utils.printConsoleAndTitle($"[{prefix}] Successfully finished phase {phaseName} without any errors.");
			Console.WriteLine($"<<==================== Finished Parallel-{phaseName} ====================>>");
			return error != 0;
		}

		private bool runSequentialPhases(IEnumerable<Phase> phases, string target, string titlePrefix, string extraParameters)
		{
			bool includeRoot = config.IncludeRootDirectory;
			string currentTargetName = Utils.extractTargetName(target);
			string archiveFileName = $"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z";

			bool errorOccurred = false;

			Console.WriteLine("<<=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-= <*> =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=>>");

			Utils.printConsoleAndTitle($"{titlePrefix} [SQN] Start processing \"{target}\"");

			foreach (Phase phase in phases)
			{
				if (phase.isTerminal)
				{
					if (terminalPhaseInclusion.Contains(target))
					{
						string? list = "";
						if (rebuildedFileLists.TryGetValue(currentTargetName, out Dictionary<string, string>? map))
							list = string.Join(" ", map.Values.ToList().ConvertAll((from) => $"-xr@\"{from}\""));
						errorOccurred = phase.performPhaseSequential(target, titlePrefix, $"{extraParameters} -r {list} -- \"{archiveFileName}\" \"{(includeRoot ? currentTargetName : "*")}\"") || errorOccurred;
					}
					else
					{
						ConsoleColor prevColor = Console.ForegroundColor;
						Console.ForegroundColor = ConsoleColor.Cyan;
						Console.WriteLine($"[SQN] Skipped terminal phase({phase.phaseName}) for \"{target}\" because all files are already processed by previous phases");
						Console.ForegroundColor = prevColor;
					}
				}
				else if (rebuildedFileLists.TryGetValue(currentTargetName, out Dictionary<string, string>? map) && map.TryGetValue(phase.phaseName, out string? fileListPath) && fileListPath != null)
					errorOccurred = phase.performPhaseSequential(target, titlePrefix, $"{extraParameters} -ir@\"{fileListPath}\" -- \"{archiveFileName}\"") || errorOccurred;
			}

			Console.WriteLine();
			Utils.getCompressionRatio(target, recursiveEnumeratorOptions, currentTargetName);
			Utils.printConsoleAndTitle($"{titlePrefix} [SQN] Finished processing {target}");

			Console.WriteLine("<<=-=-=-=-=-=-=-=-=-=-=-=-=-=-= <$> <~> =-=-=-=-=-=-=-=-=-=-=-=-=-=-=>>");

			return errorOccurred;
		}
	}
}