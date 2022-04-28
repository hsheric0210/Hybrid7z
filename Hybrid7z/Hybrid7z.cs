using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Hybrid7z
{
	public class Hybrid7z
	{
		public const string VERSION = "0.1";
		public const string CONFIG_NAME = "Hybrid7z.ini";

		public Phase[] phases;
		public string currentExecutablePath;

		private Config config;

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
				Config.saveDefaults(currentExecutablePath + CONFIG_NAME);
			}

			// Start the program
			try
			{
				new Hybrid7z().Start(currentExecutablePath, args).Wait();
			}
			catch (Exception ex)
			{
				Utils.PrintError("ERR", $"Unhandled exception caught! Please report it to the author: {ex}");
			}
		}

		private async Task InitializePhases(string logDir)
		{
			var tasks = new List<Task>();
			foreach (Phase phase in phases)
			{
				tasks.Add(Task.Run(async () =>
				{
					Utils.PrintConsole($"[P] Initializing phase: {phase.phaseName}");
					phase.Initialize(currentExecutablePath, config, logDir);
					await phase.ReadFileList();
				}));
			}

			await Task.WhenAll(tasks);
		}

		private static async Task<IEnumerable<string>> GetTargets(string[] parameters, int switchIndex)
		{
			var tasks = new List<Task<string?>>();

			for (int i = switchIndex; i < parameters.Length; i++)
			{
				string targetPath = parameters[i];
				tasks.Add(Task.Run(() =>
				{
					if (Directory.Exists(targetPath))
					{
						Console.WriteLine($"[T] Found directory - \"{targetPath}\"");
						string targetPathCopy = targetPath;
						Utils.TrimTrailingPathSeparators(ref targetPathCopy);
						return targetPathCopy;
					}
					else if (File.Exists(targetPath))
					{
						Console.WriteLine($"[T] WARNING: Currently, file are not supported (only directories are supported) - \"{targetPath}\"");
					}
					else
					{
						Console.WriteLine($"[T] WARNING: File not exists - \"{targetPath}\"");
					}

					return null;
				}));
			}

			return from list in (await Task.WhenAll(tasks)) where list is not null select list;
		}

		private async Task<bool> ProcessPhase(IEnumerable<string> targets)
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
			{
				if (phase.doesntSupportMultiThread)
					error = await RunParallelPhase(phase, targets, extraParameters) || error;
				else
					sequentialPhases.Add(phase);
			}

			// Phases with multi-thread support MUST run sequentially, Or they will crash because of insufficient RAM or other system resources. (Especially, LZMA2, Fast-LZMA2 phase)
			int totalTargetCount = targets.Count();
			int currentFileIndex = 1;
			foreach (string? target in targets)
			{
				string titlePrefix = $"[{currentFileIndex}/{totalTargetCount}]";
				error = await RunSequentialPhases(sequentialPhases, target, titlePrefix, extraParameters) || error;
				currentFileIndex++;
			}

			long original = 0;
			long compressed = 0;
			foreach (string? target in targets)
			{
				(long original, long compressed) currentRatio = Utils.GetCompressionRatio(target, recursiveEnumeratorOptions);
				original += currentRatio.original;
				compressed += currentRatio.compressed;
			}

			Utils.PrintCompressionRatio("(Overall)", original, compressed);

			return error;
		}

		private static int ParseParameters(string[] parameters)
		{
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
								return -1;
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
					{
						break;
					}
				}
			}
			return switchIndex;
		}

		private static string logRoot = "logs";

		public async Task Start(string currentExecutablePath, string[] parameters)
		{
			this.currentExecutablePath = currentExecutablePath;

			// Parse the command-line parameters
			var switchIndex = ParseParameters(parameters);
			if (switchIndex < 0)
				return;

			Config? _config = LoadConfig(currentExecutablePath);
			if (_config == null)
				return;
			config = _config;

			if (!ConstructPhases(_config.phases))
				return;

			IEnumerable<string>? targets = null;
			var parallel = new Task[2];
			parallel[0] = InitializeWholePhases();

			// Filter available targets
			parallel[1] = Task.Run(async () => targets = await GetTargets(parameters, switchIndex));

			await Task.WhenAll(parallel);

			if (targets == null)
			{
				Console.WriteLine("exit because targets is null");
				return;
			}

			// Re-build file lists
			await RebuildFileLists(targets);

			Console.WriteLine("[C] Now starting the compression...");

			ConsoleColor prevColor = Console.ForegroundColor;

			// Process phases
			if (!targets.Any())
			{
				Console.ForegroundColor = ConsoleColor.DarkYellow;
				Console.WriteLine("[C] No target supplied.");
				Console.ForegroundColor = prevColor;
			}
			else if (await ProcessPhase(targets))
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
			Utils.Pause();

			// Delete any left-over filelist files
			Parallel.ForEach(rebuildedFileLists.Values, files =>
			{
				Parallel.ForEach(files.Values, file =>
				{
					if (file != null && File.Exists(file))
					{
						File.Delete(file);
						Console.WriteLine($"[DFL] Deleted (re-builded) file list \"{file}\"");
					}
				});
			});

			Console.WriteLine("Press any key and enter to exit program...");

			// Wait until any key has been pressed
			Utils.Pause();
		}

		private async Task InitializeWholePhases()
		{
			var sw = new Stopwatch();
			// Initialize phases
			Utils.PrintConsole("[P] Initializing phases...");
			sw.Start();
			await InitializePhases(logRoot);
			sw.Stop();
			Console.WriteLine($"[P] Done initializing phases. (Took {sw.ElapsedMilliseconds}ms)");
		}

		private async Task RebuildFileLists(IEnumerable<string> targets)
		{
			const string _namespace = nameof(rebuildedFileLists);

			var sw = new Stopwatch();
			Utils.PrintConsole("Re-building file lists...", _namespace);
			try
			{
				sw.Restart();

				// KEY: target
				// VALUE: Set of available files
				ConcurrentDictionary<string, HashSet<string>> availableFiles = new();

				var tasks = new List<Task>();
				foreach (Phase phase in phases)
				{
					string phaseName = phase.phaseName;
					tasks.Add(Parallel.ForEachAsync(targets, (target, _) =>
					{
						var files = Directory
													.EnumerateFiles(target, "*", recursiveEnumeratorOptions)
													.Select(path => path.ToUpperInvariant())
													.ToHashSet();

						string targetName = Utils.ExtractTargetName(target);
						Utils.PrintConsole($"Re-building file list for [file=\"{targetName}\", phase={phaseName}]", _namespace);

						// This will remove all paths(files) matching the specified filter from availableFilesForThisTarget.
						string? path = phase.RebuildFileList(target, $"{targetName}.", recursiveEnumeratorOptions, ref files);

						// Fail-safe
						if (!rebuildedFileLists.ContainsKey(targetName))
							rebuildedFileLists[targetName] = new();

						Dictionary<string, string> map = rebuildedFileLists[targetName];
						if (path != null)
							map.Add(phaseName, path);
						availableFiles[target] = files;
						return default;
					}));
				}
				await Task.WhenAll(tasks);

				// TODO: Improve this ugly solution
				foreach (string target in targets)
				{
					if (availableFiles.TryGetValue(target, out HashSet<string>? avail) && avail.Count > 0)
						terminalPhaseInclusion.Add(target);
				}
			}
			catch (Exception ex)
			{
				Utils.PrintError(_namespace, $"Exception occurred while re-building filelists: {ex}");
			}
			sw.Stop();

			Utils.PrintConsole($"Done rebuilding file lists (Took {sw.ElapsedMilliseconds}ms)", _namespace);
		}

		private bool ConstructPhases(string[] phaseNames)
		{
			const string _namespace = nameof(ConstructPhases);
			Utils.PrintConsole("Constructing phases...", _namespace);
			try
			{
				// Construct phases
				int count = phaseNames.Length;
				phases = new Phase[count];
				for (int i = 0; i < count; i++)
				{
					string phaseName = phaseNames[i][1..];
					bool terminal = i >= count - 1;
					bool runParallel = char.ToLower(phaseNames[i][0]) == 'y';
					phases[i] = new Phase(phaseName, terminal, runParallel);
					Utils.PrintConsole($"Phase[{i}] = \"{phaseName}\" (terminal={terminal}, parallel={runParallel})", _namespace);
				}
			}
			catch (Exception ex)
			{
				Utils.PrintError(_namespace, $"Can't construct phases due exception: {ex}");
				return false;
			}

			return true;
		}

		private Config? LoadConfig(string currentExecutablePath)
		{
			const string _namespace = nameof(LoadConfig);
			Utils.PrintConsole($"Loading config... ({CONFIG_NAME})", _namespace);

			try
			{
				return new Config(new IniFile($"{currentExecutablePath}{CONFIG_NAME}"));
			}
			catch (Exception ex)
			{
				Utils.PrintError(_namespace, $"Can't load config due exception: {ex}");
			}
			return null;
		}

		private async Task<bool> RunParallelPhase(Phase phase, IEnumerable<string> paths, string extraParameters)
		{
			string phaseName = phase.phaseName;
			string prefix = $"{nameof(RunParallelPhase)}({phaseName})";

			bool includeRoot = config.IncludeRootDirectory;

			// Because Interlocked.CompareExchange doesn't supports bool
			int error = 0;

			Console.WriteLine($"<<==================== Starting Parallel({phaseName}) ====================>>");

			try
			{
				await Parallel.ForEachAsync(paths, async (path, _) =>
				{
					string currentTargetName = Utils.ExtractTargetName(path);
					if (rebuildedFileLists.TryGetValue(currentTargetName, out Dictionary<string, string>? fileListMap) && fileListMap.TryGetValue(phaseName, out string? fileListPath) && fileListPath != null && !await phase.PerformPhaseParallel(path, $"{extraParameters} -ir@\"{fileListPath}\" -- \"{$"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z"}\""))
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
				Utils.PrintConsole($"Successfully finished phase {phaseName} without any errors.", prefix);
			Console.WriteLine($"<<==================== Finished Parallel({phaseName}) ====================>>");
			return error != 0;
		}

		private async Task<bool> RunSequentialPhases(IEnumerable<Phase> phases, string target, string titlePrefix, string extraParameters)
		{
			const string _namespace = nameof(RunSequentialPhases);
			bool includeRoot = config.IncludeRootDirectory;
			string currentTargetName = Utils.ExtractTargetName(target);
			string archiveFileName = $"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z";

			bool errorOccurred = false;

			Console.WriteLine("<<=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-= <*> =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=>>");

			Utils.PrintConsole($"{titlePrefix} Start processing \"{target}\"", _namespace);

			foreach (Phase phase in phases)
			{
				if (phase.isTerminal)
				{
					if (terminalPhaseInclusion.Contains(target))
					{
						string? list = "";
						if (rebuildedFileLists.TryGetValue(currentTargetName, out Dictionary<string, string>? map))
							list = string.Join(" ", map.Values.ToList().ConvertAll((from) => $"-xr@\"{from}\""));
						errorOccurred = await phase.PerformPhaseSequential(target, titlePrefix, $"{extraParameters} -r {list} -- \"{archiveFileName}\" \"{(includeRoot ? currentTargetName : "*")}\"") || errorOccurred;
					}
					else
					{
						ConsoleColor prevColor = Console.ForegroundColor;
						Console.ForegroundColor = ConsoleColor.Cyan;
						Utils.PrintConsole($"Skipped terminal phase({phase.phaseName}) for \"{target}\" because all files are already processed by previous phases", _namespace);
						Console.ForegroundColor = prevColor;
					}
				}
				else if (rebuildedFileLists.TryGetValue(currentTargetName, out Dictionary<string, string>? map) && map.TryGetValue(phase.phaseName, out string? fileListPath) && fileListPath != null)
				{
					errorOccurred = await phase.PerformPhaseSequential(target, titlePrefix, $"{extraParameters} -ir@\"{fileListPath}\" -- \"{archiveFileName}\"") || errorOccurred;
				}
			}

			Console.WriteLine();
			Utils.GetCompressionRatio(target, recursiveEnumeratorOptions, currentTargetName);
			Utils.PrintConsole($"{titlePrefix} Finished processing {target}", _namespace);

			Console.WriteLine("<<=-=-=-=-=-=-=-=-=-=-=-=-=-=-= <$> <~> =-=-=-=-=-=-=-=-=-=-=-=-=-=-=>>");

			return errorOccurred;
		}
	}
}