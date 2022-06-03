using NLog;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Hybrid7z
{
	public class Hybrid7z
	{
		public const string VERSION = "0.1";
		public const string CONFIG_NAME = "Hybrid7z.ini";

		private readonly static Logger Logger = LogManager.GetLogger("Main");

		private Phase[] Phases;
		private string CurrentExecutablePath;

		private Config config;

		// KEY: Target Directory Name (Not Path)
		// VALUE:
		// *** KEY: Phase name
		// *** VALUE: Path of re-builded file list
		private readonly ConcurrentDictionary<string, Dictionary<string, string>> RebuildedFileLists = new();
		private readonly HashSet<string> TerminalPhaseInclusion = new();
		private readonly EnumerationOptions RecursiveEnumeratorOptions = new()
		{
			AttributesToSkip = 0,
			RecurseSubdirectories = true
		};

		public static void Main(string[] args)
		{
			Logger.Info($"Hybrid7z v{VERSION} started");

			string currentExecutablePath = AppDomain.CurrentDomain.BaseDirectory;

			// Check configuration file is exists
			if (!File.Exists(currentExecutablePath + CONFIG_NAME))
			{
				Logger.Info("[CFG] Writing default config");
				Config.SaveDefaults(currentExecutablePath + CONFIG_NAME);
			}

			// Start the program
			try
			{
				new Hybrid7z().Start(currentExecutablePath, args).Wait();
			}
			catch (Exception ex)
			{
				Logger.Error("ERR", $"Unhandled exception caught! Please report it to the author: {ex}", ex);
			}
		}

		private async Task InitializePhases(string logDir)
		{
			var tasks = new List<Task>();
			foreach (Phase phase in Phases)
			{
				tasks.Add(Task.Run(async () =>
				{
					Logger.Debug($"Initializing phase: {phase.phaseName}");
					phase.Initialize(CurrentExecutablePath, config, logDir);
					await phase.ParseFileList();
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
						Logger.Info($"Found directory - \"{targetPath}\"");
						string targetPathCopy = targetPath;
						Utils.TrimTrailingPathSeparators(ref targetPathCopy);
						return targetPathCopy;
					}
					else if (File.Exists(targetPath))
					{
						Logger.Warn($"Currently, file are not supported (only directories are supported) - \"{targetPath}\"");
					}
					else
					{
						Logger.Warn($"File not exists - \"{targetPath}\"");
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
				Directory.CreateDirectory($"{CurrentExecutablePath}logs");

			string extraParameters = "";
			if (File.Exists("Exclude.txt"))
				extraParameters = $"-xr@\"{CurrentExecutablePath}Exclude.txt\"";

			var sequentialPhases = new List<Phase>();
			foreach (Phase? phase in Phases)
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
				(long original, long compressed) currentRatio = Utils.GetCompressionRatio(target, RecursiveEnumeratorOptions);
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
								Console.WriteLine("\t/y, /yesall, /nobreak, /nopause\t\t\tNo-pause mode: Skip all pause points");
								return -1;
							}

							case "log":
							case "logdir":
							case "logroot":
							{
								if (parameters.Length > switchIndex + 1)
									logRoot = parameters[switchIndex + 1];
								else
									Logger.Info("[LogRoot] You must specify the log root folder after the logRoot switch (ex: '-logroot logs')");
								switchIndex++; // Skip trailing directory name part

								break;
							}

							case "y":
							case "yesall":
							case "nobreak":
							case "nopause":
							{
								Logger.Info("[NoPause] NoPause mode enabled.");
								Utils.NoPause = true;
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
			this.CurrentExecutablePath = currentExecutablePath;

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
				Logger.Warn("Exit program because targets is null (unexpected null)");
				return;
			}

			// Re-build file lists
			await RebuildFileLists(targets);

			Logger.Debug("Now starting the compression...");

			// Process phases
			if (!targets.Any())
				Logger.Warn("No target supplied.");
			else if (await ProcessPhase(targets))
				Logger.Warn("At least one error/warning occurred during the progress.");
			else
				Logger.Debug("DONE: All files are successfully proceed without any error(s).");

			// Wait until any key has been pressed
			Utils.Pause("Press any key and enter to delete leftover filelists...");

			// Delete any left-over filelist files
			Parallel.ForEach(RebuildedFileLists.Values, files =>
			{
				Parallel.ForEach(files.Values, file =>
				{
					if (file != null && File.Exists(file))
					{
						File.Delete(file);
						Logger.Info($"Deleted (re-builded) file list \"{file}\"");
					}
				});
			});

			// Wait until any key has been pressed
			Utils.Pause("Press any key and enter to exit program...");
		}

		private async Task InitializeWholePhases()
		{
			var sw = new Stopwatch();
			// Initialize phases
			Logger.Info("Initializing phases...");
			sw.Start();
			await InitializePhases(logRoot);
			sw.Stop();
			Logger.Info($"Done initializing phases. (Took {sw.ElapsedMilliseconds}ms)");
		}

		private async Task RebuildFileLists(IEnumerable<string> targets)
		{
			var sw = new Stopwatch();
			Logger.Debug("Re-building file lists...");
			try
			{
				sw.Restart();

				// KEY: target
				// VALUE: Set of available files
				ConcurrentDictionary<string, HashSet<string>> availableFiles = new();

				var tasks = new List<Task>();
				foreach (Phase phase in Phases)
				{
					string phaseName = phase.phaseName;
					tasks.Add(Parallel.ForEachAsync(targets, (target, _) =>
					{
						var files = Directory
													.EnumerateFiles(target, "*", RecursiveEnumeratorOptions)
													.Select(path => path.ToUpperInvariant())
													.ToHashSet();

						string targetName = Utils.ExtractTargetName(target);
						Logger.Info($"Re-building file list for [file=\"{targetName}\", phase={phaseName}]");

						// This will remove all paths(files) matching the specified filter from availableFilesForThisTarget.
						string? path = phase.RebuildFileList(target, $"{targetName}.", RecursiveEnumeratorOptions, ref files);

						// Fail-safe
						if (!RebuildedFileLists.ContainsKey(targetName))
							RebuildedFileLists[targetName] = new();

						Dictionary<string, string> map = RebuildedFileLists[targetName];
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
						TerminalPhaseInclusion.Add(target);
				}
			}
			catch (Exception ex)
			{
				Logger.Error($"Exception occurred while re-building filelists: {ex}");
			}
			sw.Stop();

			Logger.Info($"Done rebuilding file lists (Took {sw.ElapsedMilliseconds}ms)");
		}

		private bool ConstructPhases(string[] phaseNames)
		{
			try
			{
				// Construct phases
				int count = phaseNames.Length;
				Phases = new Phase[count];
				for (int i = 0; i < count; i++)
				{
					string phaseName = phaseNames[i][1..];
					bool terminal = i >= count - 1;
					bool runParallel = char.ToLower(phaseNames[i][0]) == 'y';
					Phases[i] = new Phase(phaseName, terminal, runParallel);
					Logger.Info($"Phase[{i}] = \"{phaseName}\" (terminal={terminal}, parallel={runParallel})");
				}
			}
			catch (Exception ex)
			{
				Logger.Error("Can't construct phases due exception", ex);
				return false;
			}

			return true;
		}

		private static Config? LoadConfig(string currentExecutablePath)
		{
			Logger.Info($"Loading config... ({CONFIG_NAME})");

			try
			{
				return new Config(new IniFile($"{currentExecutablePath}{CONFIG_NAME}"));
			}
			catch (Exception ex)
			{
				Logger.Error("Can't load config due exception", ex);
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
					if (RebuildedFileLists.TryGetValue(currentTargetName, out Dictionary<string, string>? fileListMap) && fileListMap.TryGetValue(phaseName, out string? fileListPath) && fileListPath != null && !await phase.PerformPhaseParallel(path, $"{extraParameters} -ir@\"{fileListPath}\" -- \"{$"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z"}\""))
						Interlocked.CompareExchange(ref error, 1, 0);
				});
			}
			catch (AggregateException ex)
			{
				Logger.Error("AggregateException occurred during parallel execution".WithNamespace(prefix), ex);
				error = 1;
			}

			if (error != 0)
				Logger.Error($"Error running phase {phaseName} in parallel.".WithNamespace(prefix));
			else
				Logger.Info($"Successfully finished phase {phaseName} without any errors.".WithNamespace(prefix));
			Console.WriteLine($"<<==================== Finished Parallel({phaseName}) ====================>>");
			return error != 0;
		}

		private async Task<bool> RunSequentialPhases(IEnumerable<Phase> phases, string target, string titlePrefix, string extraParameters)
		{
			bool includeRoot = config.IncludeRootDirectory;
			string currentTargetName = Utils.ExtractTargetName(target);
			string archiveFileName = $"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z";

			bool errorOccurred = false;

			Console.WriteLine("<<=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-= <*> =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=>>");

			Logger.Info($"{titlePrefix} Start processing \"{target}\"");

			foreach (Phase phase in phases)
			{
				if (phase.isTerminal)
				{
					if (TerminalPhaseInclusion.Contains(target))
					{
						string? list = "";
						if (RebuildedFileLists.TryGetValue(currentTargetName, out Dictionary<string, string>? map))
							list = string.Join(" ", map.Values.ToList().ConvertAll((from) => $"-xr@\"{from}\""));
						errorOccurred = await phase.PerformPhaseSequential(target, titlePrefix, $"{extraParameters} -r {list} -- \"{archiveFileName}\" \"{(includeRoot ? currentTargetName : "*")}\"") || errorOccurred;
					}
					else
					{
						Logger.Warn($"Skipped terminal phase({phase.phaseName}) for \"{target}\" because all files are already processed by previous phases");
					}
				}
				else if (RebuildedFileLists.TryGetValue(currentTargetName, out Dictionary<string, string>? map) && map.TryGetValue(phase.phaseName, out string? fileListPath) && fileListPath != null)
				{
					errorOccurred = await phase.PerformPhaseSequential(target, titlePrefix, $"{extraParameters} -ir@\"{fileListPath}\" -- \"{archiveFileName}\"") || errorOccurred;
				}
			}

			Console.WriteLine();
			Utils.GetCompressionRatio(target, RecursiveEnumeratorOptions, currentTargetName);
			Logger.Debug($"{titlePrefix} Finished processing {target}");

			Console.WriteLine("<<=-=-=-=-=-=-=-=-=-=-=-=-=-=-= <$> <~> =-=-=-=-=-=-=-=-=-=-=-=-=-=-=>>");

			return errorOccurred;
		}
	}
}