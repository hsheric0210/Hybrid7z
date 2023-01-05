using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace Hybrid7z
{
	public class Hybrid7z
	{
		public const string VERSION = "0.1";
		public const string CONFIG_NAME = "Hybrid7z.ini";
		public const string LogFileName = "Hybrid7z.log";

		private IList<Phase> PhaseList;
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
			try
			{
				Log.Logger = new LoggerConfiguration()
					.MinimumLevel.Verbose()
					.WriteTo.Async(a => a.File(LogFileName, fileSizeLimitBytes: 268435456, rollOnFileSizeLimit: true, buffered: true, flushToDiskInterval: TimeSpan.FromSeconds(2)))
					.WriteTo.Async(a => a.Console(theme: AnsiConsoleTheme.Code))
					.CreateLogger();
			}
			catch (Exception ex)
			{
				Console.WriteLine("Logger creation failure.");
				Console.WriteLine(ex);
			}

			Log.Information($"Hybrid7z v{VERSION} started on {{date}}", DateTime.Now);

			var cwd = AppDomain.CurrentDomain.BaseDirectory;

			// Check configuration file is exists
			var configPath = cwd + CONFIG_NAME;
			if (!File.Exists(configPath))
			{
				Log.Warning("Configuration file not found! Writing default configuration file to: {file}", configPath);
				Config.SaveDefaults(configPath);
			}

			// Start the program
			try
			{
				new Hybrid7z().Start(cwd, args).Wait();
			}
			catch (Exception ex)
			{
				Log.Fatal(ex, "Unhandled exception caught! Please report to the author.");
			}
		}

		private async Task InitializePhases(string logDir)
		{
			var tasks = new List<Task>();
			foreach (Phase phase in PhaseList)
			{
				tasks.Add(Task.Run(async () =>
				{
					Log.Debug("Phase initialization: {phase}", phase.phaseName);
					phase.Initialize(CurrentExecutablePath, config, logDir);
					Log.Information("Phase initialized: {phase}", phase.phaseName);
					await phase.ParseFileList();
				}));
			}
			await Task.WhenAll(tasks);
		}

		private static async Task<ICollection<string>> GetTargets(string[] parameters, int switchIndex)
		{
			var tasks = new List<Task<string?>>();
			for (var i = switchIndex; i < parameters.Length; i++)
			{
				tasks.Add(Task.Run(() =>
				{
					var path = parameters[i];
					Utils.TrimTrailingPathSeparators(ref path);
					if (Directory.Exists(path))
					{
						Log.Information("Found directory: {path}", path);
						return path;
					}
					else if (File.Exists(path))
					{
						// FIXME: File support
						Log.Warning("Currently, file are not supported (only directories are supported): {path}", path);
					}
					else
					{
						Log.Warning("Filesystem entry not exists: {path}", path);
					}

					return null;
				}));
			}
			return (from list in await Task.WhenAll(tasks) where list is not null select list).ToList();
		}

		private async Task<bool> ProcessPhase(ICollection<string> targets)
		{
			var error = false;

			// Create log file repository
			if (targets.Any())
				Directory.CreateDirectory($"{CurrentExecutablePath}logs");

			var extraParameters = "";
			if (File.Exists("Exclude.txt"))
				extraParameters = $"-xr@\"{CurrentExecutablePath}Exclude.txt\"";

			var sequentialPhases = new List<Phase>();
			foreach (Phase? phase in PhaseList)
			{
				if (phase.doesntSupportMultiThread)
					error = await RunParallelPhase(phase, targets, extraParameters) || error;
				else
					sequentialPhases.Add(phase);
			}

			// Phases with multi-thread support MUST run sequentially, Or they will crash because of insufficient RAM or other system resources. (Especially, LZMA2, Fast-LZMA2 phase)
			var totalTargetCount = targets.Count;
			var currentFileIndex = 1;
			foreach (var target in targets)
			{
				var titlePrefix = $"[{currentFileIndex}/{totalTargetCount}]";
				error = await RunSequentialPhases(sequentialPhases, target, titlePrefix, extraParameters) || error;
				currentFileIndex++;
			}

			long original = 0;
			long compressed = 0;
			foreach (var target in targets)
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
			var switchIndex = 0;
			if (parameters.Length > 0)
			{
				for (var n = parameters.Length; switchIndex < n; switchIndex++)
				{
					var rawParam = parameters[switchIndex];
					if (rawParam.StartsWith('/') || rawParam.StartsWith('-'))
					{
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
								Console.WriteLine("No switch prefix and switch prefix '-', '/' is all supported: 'help' and '-help' and '/help' do the same thing");
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
									Log.Warning("You must specify the log root folder after the logRoot switch (ex: '-logroot logs')");
								switchIndex++; // Skip trailing directory name part
								break;
							}

							case "y":
							case "yesall":
							case "nobreak":
							case "nopause":
							{
								Log.Warning("No-Pause mode enabled. All user prompts will be assumed as 'Yes'.");
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

		public async Task Start(string cwd, string[] parameters)
		{
			this.CurrentExecutablePath = cwd;

			// Parse the command-line parameters
			var switchIndex = ParseParameters(parameters);
			if (switchIndex < 0)
				return;

			Config? _config = LoadConfig(cwd);
			if (_config == null)
				return;
			config = _config;

			if (!ConstructPhases(_config.PhaseList))
				return;

			ICollection<string>? targets = null;
			var parallel = new Task[2];
			parallel[0] = InitializeWholePhases();

			// Filter available targets
			parallel[1] = Task.Run(async () => targets = await GetTargets(parameters, switchIndex));
			await Task.WhenAll(parallel);

			// Cannot be triggered
			if (targets is null)
				return;

			await RebuildFileLists(targets);

			Log.Debug("+++ Compression");
			if (targets.Count == 0)
				Log.Error("No target supplied.");
			else if (await ProcessPhase(targets))
				Log.Warning("At least one error/warning occurred during the progress.");
			else
				Log.Debug("DONE: All files are successfully proceed without any error(s).");

			Parallel.ForEach(RebuildedFileLists.Values, files =>
			{
				Parallel.ForEach(files.Values, file =>
				{
					if (file != null && File.Exists(file))
					{
						Log.Debug("Rebuilded phase filter deletion: {path}", file);
						File.Delete(file);
					}
				});
			});

			// Wait until any key has been pressed
			Utils.UserInput("Press any key and enter to exit program...");
		}

		private async Task InitializeWholePhases()
		{
			var sw = new Stopwatch();
			// Initialize phases
			Log.Debug("+++ Phase initialization");
			sw.Start();
			await InitializePhases(logRoot);
			sw.Stop();
			Log.Information("--- Phase initialization: {took}ms", sw.ElapsedMilliseconds);
		}

		private async Task RebuildFileLists(IEnumerable<string> targets)
		{
			var sw = new Stopwatch();
			Log.Debug("+++ Phase filter rebuilding");
			try
			{
				sw.Restart();

				// KEY: target
				// VALUE: Set of available files
				ConcurrentDictionary<string, HashSet<string>> availableFiles = new();

				var tasks = new List<Task>();
				foreach (Phase phase in PhaseList)
				{
					var phaseName = phase.phaseName;
					tasks.Add(Parallel.ForEachAsync(targets, (target, _) =>
					{
						// FIXME: 얘 원래 여기가 아니라 Phase foreach 바깥에 있어야지 원래 의도한대로 'RebuildFileList에서 필터에 맞는 파일들 쳐낸 후, files에서 해당 파일 공제'함으로써 '이미 어떤 페이즈를 통해 처리된 파일이 다른 페이즈를 통해 다시 처리되는 것을 방지'하는 역할을 수행할 수 있는 것 아닌가?
						var files = Directory
							.EnumerateFiles(target, "*", RecursiveEnumeratorOptions)
							.Select(path => path.ToUpperInvariant())
							.ToHashSet();

						var targetName = Utils.ExtractTargetName(target);
						Log.Debug("Phase {phase} filter rebuild: {path}", phaseName, targetName);

						// This will remove all paths(files) matching the specified filter from availableFilesForThisTarget.
						var filterPath = phase.RebuildFilter(target, $"{targetName}.", RecursiveEnumeratorOptions, ref files);

						// Fail-safe
						if (!RebuildedFileLists.ContainsKey(targetName))
							RebuildedFileLists[targetName] = new();

						Dictionary<string, string> map = RebuildedFileLists[targetName];
						if (filterPath != null)
							map.Add(phaseName, filterPath);
						availableFiles[target] = files;
						return default;
					}));
				}
				await Task.WhenAll(tasks);

				// TODO: Improve this ugly solution
				foreach (var target in targets)
				{
					if (availableFiles.TryGetValue(target, out HashSet<string>? avail) && avail.Count > 0)
						TerminalPhaseInclusion.Add(target);
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Exception during phase filter rebuilding.");
			}
			sw.Stop();

			Log.Information("--- Phase filter rebuilding: {took}ms", sw.ElapsedMilliseconds);
		}

		private bool ConstructPhases(IReadOnlyList<string> phaseList)
		{
			try
			{
				var count = phaseList.Count;
				PhaseList = new Phase[count];
				for (var i = 0; i < count; i++)
				{
					var phaseName = phaseList[i];
					var isTerminal = i == count - 1;
					var parallel = config.IsPhaseParallel(phaseList[i]);
					PhaseList[i] = new Phase(phaseName, isTerminal, parallel);
					Log.Information("Detected phase: name={name}, terminal={terminal}, parallel={parallel}", phaseName, isTerminal, parallel);
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Exception during phase construction.");
				return false;
			}

			return true;
		}

		private static Config? LoadConfig(string currentExecutablePath)
		{
			Log.Information("+++ Config loading");

			try
			{
				return new Config(currentExecutablePath + CONFIG_NAME);
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Exception during config loading.");
			}
			return null;
		}

		private async Task<bool> RunParallelPhase(Phase phase, IEnumerable<string> paths, string extraParameters)
		{
			var phaseName = phase.phaseName;
			var includeRoot = config.IncludeRootDirectory;

			// Because Interlocked.CompareExchange doesn't supports bool
			var error = 0;

			Log.Debug("+++ Phase parallel execution: {phase}", phaseName);
			try
			{
				await Parallel.ForEachAsync(paths, async (path, _) =>
				{
					var currentTargetName = Utils.ExtractTargetName(path);
					if (RebuildedFileLists.TryGetValue(currentTargetName, out Dictionary<string, string>? fileListMap) && fileListMap.TryGetValue(phaseName, out var fileListPath) && fileListPath != null && !await phase.PerformPhaseParallel(path, $"{extraParameters} -ir@\"{fileListPath}\" -- \"{$"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z"}\""))
						Interlocked.CompareExchange(ref error, 1, 0);
				});
			}
			catch (AggregateException ex)
			{
				Log.Error(ex, "AggregateException during parallel execution of phase: {phase}", phaseName);
				error = 1;
			}

			if (error != 0)
				Log.Error("Error during parallel execution of phase: {phase}", phaseName);
			Log.Information("--- Phase parallel execution: {phase}", phaseName);
			return error != 0;
		}

		private async Task<bool> RunSequentialPhases(IEnumerable<Phase> phases, string target, string _, string extraParameters)
		{
			var includeRoot = config.IncludeRootDirectory;
			var currentTargetName = Utils.ExtractTargetName(target);
			var archiveFileName = $"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z";

			var errorOccurred = false;

			Log.Debug("+++ Phase sequential execution: {target}", target);

			foreach (Phase phase in phases)
			{
				if (phase.isTerminal)
				{
					if (TerminalPhaseInclusion.Contains(target))
					{
						var list = "";
						if (RebuildedFileLists.TryGetValue(currentTargetName, out Dictionary<string, string>? map))
							list = string.Join(" ", map.Values.ToList().ConvertAll((from) => $"-xr@\"{from}\""));
						errorOccurred = await phase.PerformPhaseSequential(target, _, $"{extraParameters} -r {list} -- \"{archiveFileName}\" \"{(includeRoot ? currentTargetName : "*")}\"") || errorOccurred;
					}
					else
					{
						Log.Warning("Terminal phase ({phase}) skip: {target}", phase.phaseName, target);
					}
				}
				else if (RebuildedFileLists.TryGetValue(currentTargetName, out Dictionary<string, string>? map) && map.TryGetValue(phase.phaseName, out var fileListPath) && fileListPath != null)
				{
					errorOccurred = await phase.PerformPhaseSequential(target, _, $"{extraParameters} -ir@\"{fileListPath}\" -- \"{archiveFileName}\"") || errorOccurred;
				}
			}

			Console.WriteLine();
			Utils.GetCompressionRatio(target, RecursiveEnumeratorOptions, currentTargetName);
			Log.Information("+++ Phase sequential execution: {target}", target);

			return errorOccurred;
		}
	}
}