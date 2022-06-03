using NLog;
using System.Diagnostics;
using System.Text;

namespace Hybrid7z
{
	public class Phase
	{
		public const string FILELIST_SUFFIX = ".txt";
		public const string REBUILDED_FILELIST_SUFFIX = ".lst";

		private const string SEVENZIP_STDOUT_LOG_NAME = "{0:yyyy-MM-dd_HH-mm-ss-FFFFFF}-{1}_{2}({3})-STDOUT.log";
		private const string SEVENZIP_STDERR_LOG_NAME = "{0:yyyy-MM-dd_HH-mm-ss-FFFFFF}-{1}_{2}({3})-STDERR.log";

		private static readonly UTF8Encoding UTF_8_WITHOUT_BOM = new(false);
		private static readonly FileStreamOptions LOG_FILE_STREAM_OPTIONS = new()
		{
			Access = FileAccess.Write,
			Mode = FileMode.Create
		};

		public readonly string phaseName;
		public readonly bool isTerminal;
		public readonly bool doesntSupportMultiThread;

		private Logger Logger;

		private string? CurrentExecutablePath;
		private string? PhaseParameter;
		private Config? Config;
		private string? LogFileDirectoryName;
		private string[]? FilterElements;

		public Phase(string phaseName, bool isTerminal, bool doesntSupportMultiThread)
		{
			this.phaseName = phaseName;
			this.isTerminal = isTerminal;
			this.doesntSupportMultiThread = doesntSupportMultiThread;
			Logger = LogManager.GetLogger(phaseName);
		}

		public void Initialize(string currentExecutablePath, Config config, string logFileDirectoryName)
		{
			this.CurrentExecutablePath = currentExecutablePath;
			this.Config = config;
			this.LogFileDirectoryName = logFileDirectoryName;
			PhaseParameter = config.GetPhaseSpecificParameters(phaseName);
		}

		public async Task ParseFileList()
		{
			if (isTerminal)
				return;

			string fileListPath = $"{CurrentExecutablePath}{phaseName}{FILELIST_SUFFIX}";
			if (File.Exists(fileListPath))
			{
				Logger.Info($"Parsing file list: '{fileListPath}'");

				try
				{
					string[] lines = await File.ReadAllLinesAsync(fileListPath);
					static string DropLeadingPathSeparators(string trimmed)
					{
						Utils.TrimLeadingPathSeparators(ref trimmed);
						return trimmed;
					}
					FilterElements = (from line in lines
									  let commentRemoved = line.Contains("//") ? line[..line.IndexOf("//")] : line
									  where !string.IsNullOrWhiteSpace(commentRemoved)
									  select DropLeadingPathSeparators(commentRemoved.Trim())).ToArray();
				}
				catch (Exception ex)
				{
					Logger.Error("Error reading file list", ex);
				}
			}
			else
			{
				Logger.Warn($"Phase filter file not found for phase: '{fileListPath}'");
			}
		}

		public string? RebuildFileList(string path, string fileNamePrefix, EnumerationOptions recursiveEnumeratorOptions, ref HashSet<string>? availableFilesForThisTarget)
		{
			if (isTerminal || Config == null || FilterElements == null)
				return null;

			bool includeRoot = Config.IncludeRootDirectory;
			string targetDirectoryName = Utils.ExtractTargetName(path);

			var newFilterElements = new List<string>();
			HashSet<string>? availableFilesForThisTarget_ = availableFilesForThisTarget;
			Parallel.ForEach(FilterElements, filter =>
			{
				try
				{
					if (!filter.Contains('\\') || Directory.Exists(path + '\\' + Utils.ExtractSuperDirectoryName(filter)))
					{
						IEnumerable<string> files = Directory.EnumerateFiles(path, filter, recursiveEnumeratorOptions);
						if (files.Any())
						{
							Logger.Info($"Found files for filter '{filter}'");
							newFilterElements.Add((includeRoot ? targetDirectoryName + "\\" : "") + filter);

							if (availableFilesForThisTarget_ != null)
							{
								lock (availableFilesForThisTarget_)
								{
									foreach (string filepath in files)
										availableFilesForThisTarget_.Remove(filepath.ToUpperInvariant()); // Very inefficient solution; But, at least, hey, it iss working!
								}
							}
						}
					}
				}
				catch (Exception ex)
				{
					Logger.Error("Error re-building file list", ex);
				}
			});

			string? fileListPath = null;

			if (newFilterElements.Count > 0)
			{
				try
				{
					File.WriteAllLines(fileListPath = CurrentExecutablePath + fileNamePrefix + phaseName + REBUILDED_FILELIST_SUFFIX, newFilterElements);
				}
				catch (Exception ex)
				{
					Logger.Error("Error writing re-builded file list", ex);
				}
			}

			return fileListPath;
		}

		public async Task<bool> PerformPhaseParallel(string path, string extraParameters)
		{
			if (Config == null)
				return false;

			string currentTargetName = Utils.ExtractTargetName(path);
			string _namespace = $"{nameof(PerformPhaseParallel)}-{phaseName}(\"{currentTargetName}\")";
			DateTime dateTime = DateTime.Now;

			try
			{
				Process sevenzip = new();
				sevenzip.StartInfo.FileName = Config.Get7zExecutable(phaseName);
				sevenzip.StartInfo.WorkingDirectory = $"{(Config.IncludeRootDirectory ? Utils.ExtractSuperDirectoryName(path) : path)}\\";
				sevenzip.StartInfo.Arguments = $"{Config.CommonArguments} {PhaseParameter} {extraParameters}";
				sevenzip.StartInfo.UseShellExecute = false;
				sevenzip.StartInfo.RedirectStandardOutput = true;
				sevenzip.StartInfo.RedirectStandardError = true;

				sevenzip.Start();

				string outFileNameFormatted = string.Format(SEVENZIP_STDOUT_LOG_NAME, dateTime, sevenzip.Id, phaseName, currentTargetName);
				string errFileNameFormatted = string.Format(SEVENZIP_STDERR_LOG_NAME, dateTime, sevenzip.Id, phaseName, currentTargetName);
				string outFilePath = $"{CurrentExecutablePath}{LogFileDirectoryName}\\{outFileNameFormatted}";
				string errFilePath = $"{CurrentExecutablePath}{LogFileDirectoryName}\\{errFileNameFormatted}";

				// Redirect STDOUT, STDERR
				(StringBuilder stdoutBuffer, StringBuilder stderrBuffer) = AttachLogBuffer(sevenzip, false);
				sevenzip.BeginOutputReadLine();
				sevenzip.BeginErrorReadLine();

				await sevenzip.WaitForExitAsync();

				int errorCode = sevenzip.ExitCode;
				if (errorCode != 0)
				{
					Console.WriteLine();
					Logger.Warn($"7z process exited with errors/warnings. Error code: {errorCode} - '{Utils.Get7ZipExitCodeInformation(errorCode)}'. Check the following log files for more detailed informations.".WithNamespace(_namespace));
				}

				var logTasks = new Task<bool>[2];
				logTasks[0] = Write7zLog(outFilePath, stdoutBuffer, "STDOUT", _namespace);
				logTasks[1] = Write7zLog(errFilePath, stderrBuffer, "STDERR", _namespace);
				await Task.WhenAll(logTasks);

				return errorCode == 0;
			}
			catch (Exception ex)
			{
				Console.WriteLine();
				Logger.Error("Exception while executing 7z in parallel".WithNamespace(_namespace), ex);
				return false;
			}
		}

		public async Task<bool> PerformPhaseSequential(string path, string indexPrefix, string extraParameters)
		{
			if (Config == null)
				return false;

			const string _namespace = nameof(PerformPhaseSequential);
			string currentTargetName = Utils.ExtractTargetName(path);
			int errorCode = -1;
			DateTime dateTime = DateTime.Now;

			Console.WriteLine($">> ===== -----<< {phaseName} Phase (Sequential) >>----- ===== <<");
			Logger.Info($"{indexPrefix} Started {indexPrefix} \"{currentTargetName}\" - {phaseName} Phase".WithNamespaceAndTitle(_namespace));
			Console.WriteLine();

			try
			{
				Process sevenzip = new();
				sevenzip.StartInfo.FileName = Config.Get7zExecutable(phaseName);
				sevenzip.StartInfo.WorkingDirectory = $"{(Config.IncludeRootDirectory ? Utils.ExtractSuperDirectoryName(path) : path)}\\";
				sevenzip.StartInfo.Arguments = $"{Config.CommonArguments} {PhaseParameter} {extraParameters}";
				sevenzip.StartInfo.UseShellExecute = false;
				sevenzip.StartInfo.RedirectStandardOutput = true;
				sevenzip.StartInfo.RedirectStandardError = true;

				sevenzip.Start();

				string outFileNameFormatted = string.Format(SEVENZIP_STDOUT_LOG_NAME, dateTime, sevenzip.Id, phaseName, currentTargetName);
				string errFileNameFormatted = string.Format(SEVENZIP_STDERR_LOG_NAME, dateTime, sevenzip.Id, phaseName, currentTargetName);
				string outFilePath = $"{CurrentExecutablePath}{LogFileDirectoryName}\\{outFileNameFormatted}";
				string errFilePath = $"{CurrentExecutablePath}{LogFileDirectoryName}\\{errFileNameFormatted}";

				// Redirect STDOUT, STDERR
				(StringBuilder stdoutBuffer, StringBuilder stderrBuffer) = AttachLogBuffer(sevenzip, true);
				sevenzip.BeginOutputReadLine();
				sevenzip.BeginErrorReadLine();

				await sevenzip.WaitForExitAsync();

				// Write log buffer to the file and print the path
				try
				{
					var logTasks = new Task<bool>[2];
					logTasks[0] = Write7zLog(outFilePath, stdoutBuffer, "STDOUT", _namespace);
					logTasks[1] = Write7zLog(errFilePath, stderrBuffer, "STDERR", _namespace);
					await Task.WhenAll(logTasks);
				}
				catch (Exception ex)
				{
					Logger.Error($"{indexPrefix} Failed to write log 7z log files: {ex}".WithNamespace(_namespace), ex);
				}

				errorCode = sevenzip.ExitCode;
			}
			catch (Exception ex)
			{
				Console.WriteLine();
				Logger.Error($"{indexPrefix} Exception while executing 7z".WithNamespace(_namespace), ex);
			}

			bool error = errorCode != 0;

			if (error)
			{
				Console.WriteLine();
				Console.Title = $"{_namespace} {indexPrefix} Error compressing \"{path}\" - {phaseName} phase";
				Logger.Warn($"7z process exited with errors/warnings. Error code: {errorCode} ({Utils.Get7ZipExitCodeInformation(errorCode)}). Check the following log files for more detailed informations.".WithNamespace(_namespace));
			}

			Console.WriteLine();
			Logger.Debug($"{indexPrefix} \"{currentTargetName}\" - {phaseName} Phase Finished.".WithNamespace(_namespace));

			return error;
		}

		private async Task<bool> Write7zLog(string path, StringBuilder sb, string? displayName = null, string? _namespace = null)
		{
			if (sb.Length > 0)
			{
				using (var stream = new StreamWriter(path, UTF_8_WITHOUT_BOM, LOG_FILE_STREAM_OPTIONS))
					await stream.WriteAsync(sb.ToString());

				if (displayName != null)
					Logger.Debug($"{displayName} log: \"{path}\"".WithNamespace(_namespace));

				return true;
			}

			return false;
		}

		// wtf formatter?
		private static DataReceivedEventHandler GetBufferRedirectHandler(StringBuilder buffer, bool alsoConsole) => new((_, param) =>
																																		   {
																																			   string? data = param.Data;
																																			   if (!string.IsNullOrEmpty(data))
																																			   {
																																				   buffer.AppendLine(data);
																																				   if (alsoConsole)
																																					   Console.WriteLine("[7z] " + data);
																																			   }
																																		   });

		private static (StringBuilder, StringBuilder) AttachLogBuffer(Process process, bool alsoConsole)
		{
			var stdoutBuffer = new StringBuilder();
			var stderrBuffer = new StringBuilder();
			process.OutputDataReceived += GetBufferRedirectHandler(stdoutBuffer, alsoConsole);
			process.ErrorDataReceived += GetBufferRedirectHandler(stderrBuffer, alsoConsole);
			return (stdoutBuffer, stderrBuffer);
		}
	}
}
