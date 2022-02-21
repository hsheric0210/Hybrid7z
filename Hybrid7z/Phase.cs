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

		public string? currentExecutablePath;
		public string? phaseParameter;
		public Config? config;
		public string? logFileDirectoryName;

		public string[]? filterElements;

		public Phase(string phaseName, bool isTerminal, bool doesntSupportMultiThread)
		{
			this.phaseName = phaseName;
			this.isTerminal = isTerminal;
			this.doesntSupportMultiThread = doesntSupportMultiThread;
		}

		public void init(string currentExecutablePath, Config config, string logFileDirectoryName)
		{
			this.currentExecutablePath = currentExecutablePath;
			this.config = config;
			this.logFileDirectoryName = logFileDirectoryName;
			phaseParameter = config.getPhaseSpecificParameters(phaseName);
		}

		public void readFileList()
		{
			if (isTerminal)
				return;

			string filelistPath = currentExecutablePath + phaseName + FILELIST_SUFFIX;
			if (File.Exists(filelistPath))
			{
				Utils.printConsoleAndTitle($"[RFL] Reading file list: \"{filelistPath}\"");

				try
				{
					string[] lines = File.ReadAllLines(filelistPath);
					var validElements = new List<string>(lines.Length);
					foreach (string line in lines)
					{
						string commentRemoved = line.Contains("//") ? line[..line.IndexOf("//")] : line;
						if (!string.IsNullOrWhiteSpace(commentRemoved))
						{
							// Console.WriteLine($"[RFL] Readed from {filelistPath}: \"{commentRemoved}\"");
							var trimmed = commentRemoved.Trim();
							Utils.trimLeadingPathSeparators(ref trimmed);
							validElements.Add(trimmed);
						}
					}
					filterElements = validElements.ToArray();
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[RFL] Error reading file list: {ex}");
				}
			}
			else
				Console.WriteLine($"[RFL] Phase filter file not found for phase: {filelistPath}");
		}

		public string? rebuildFileList(string path, string fileNamePrefix, EnumerationOptions recursiveEnumeratorOptions, ref HashSet<string>? availableFilesForThisTarget)
		{
			if (isTerminal || config == null || filterElements == null)
				return null;

			bool includeRoot = config.IncludeRootDirectory;
			string targetDirectoryName = Utils.extractTargetName(path);

			var newFilterElements = new List<string>();
			var tasks = new List<Task>();
			HashSet<string>? availableFilesForThisTarget_ = availableFilesForThisTarget;
			Parallel.ForEach(filterElements, filter =>
			{
				try
				{
					if ((!filter.Contains('\\') || Directory.Exists(path + '\\' + Utils.extractSuperDirectoryName(filter))))
					{
						IEnumerable<string> files = Directory.EnumerateFiles(path, filter, recursiveEnumeratorOptions);
						if (files.Any())
						{
							Console.WriteLine($"[RbFL] Found files for filter \"{filter}\"");
							newFilterElements.Add((includeRoot ? targetDirectoryName + "\\" : "") + filter);

							if (availableFilesForThisTarget_ != null)
								lock (availableFilesForThisTarget_)
									foreach (string filepath in files)
										availableFilesForThisTarget_.Remove(filepath.ToUpperInvariant()); // Very inefficient solution; But, at least, hey, it iss working!
						}
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[RbFL] Error re-building file list: {ex}");
				}
			});

			string? fileListPath = null;

			if (newFilterElements.Any())
			{
				try
				{
					File.WriteAllLines(fileListPath = currentExecutablePath + fileNamePrefix + phaseName + REBUILDED_FILELIST_SUFFIX, newFilterElements);
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[RbFL] Error writing re-builded file list: {ex}");
				}
			}

			return fileListPath;
		}

		public bool performPhaseParallel(string path, string extraParameters)
		{
			if (config == null)
				return false;

			string currentTargetName = Utils.extractTargetName(path);
			string prefix = $"[PRL-{phaseName}(\"{currentTargetName}\")]";
			DateTime dateTime = DateTime.Now;

			try
			{
				Process sevenzip = new();
				sevenzip.StartInfo.FileName = config.get7zExecutable(phaseName);
				sevenzip.StartInfo.WorkingDirectory = $"{(config.IncludeRootDirectory ? Utils.extractSuperDirectoryName(path) : path)}\\";
				sevenzip.StartInfo.Arguments = $"{config.CommonArguments} {phaseParameter} {extraParameters}";
				sevenzip.StartInfo.UseShellExecute = false;
				sevenzip.StartInfo.RedirectStandardOutput = true;
				sevenzip.StartInfo.RedirectStandardError = true;

				sevenzip.Start();

				string outFileNameFormatted = string.Format(SEVENZIP_STDOUT_LOG_NAME, dateTime, sevenzip.Id, phaseName, currentTargetName);
				string errFileNameFormatted = string.Format(SEVENZIP_STDERR_LOG_NAME, dateTime, sevenzip.Id, phaseName, currentTargetName);
				string outFilePath = $"{currentExecutablePath}{logFileDirectoryName}\\{outFileNameFormatted}";
				string errFilePath = $"{currentExecutablePath}{logFileDirectoryName}\\{errFileNameFormatted}";

				// Redirect STDOUT, STDERR
				(StringBuilder stdoutBuffer, StringBuilder stderrBuffer) = attachLogBuffer(sevenzip, false);
				sevenzip.BeginOutputReadLine();
				sevenzip.BeginErrorReadLine();

				sevenzip.WaitForExit();

				int errorCode = sevenzip.ExitCode;
				if (errorCode != 0)
				{
					Console.WriteLine();
					Console.WriteLine($"{prefix} 7z process exited with errors/warnings. Error code: {errorCode} - {Utils.get7ZipExitCodeInformation(errorCode)}. Check the following log files for more detailed informations.");
				}

				// Write log buffer to the file and print the path
				if (writeLog(outFilePath, stdoutBuffer))
					Console.WriteLine($"{prefix} STDOUT log: \"{outFilePath}\"");
				if (writeLog(errFilePath, stderrBuffer))
					Console.WriteLine($"{prefix} STDERR log: \"{errFilePath}\"");

				return errorCode == 0;
			}
			catch (Exception ex)
			{
				Console.WriteLine();
				Console.WriteLine($"{prefix} Exception while executing 7z in parallel: {ex}");
				return false;
			}
		}

		public bool performPhaseSequential(string path, string indexPrefix, string extraParameters)
		{
			if (config == null)
				return false;

			string currentTargetName = Utils.extractTargetName(path);
			int errorCode = -1;
			DateTime dateTime = DateTime.Now;

			Console.WriteLine($">> ===== -----<< {phaseName} Phase (Sequential) >>----- ===== <<");
			Utils.printConsoleAndTitle($"{indexPrefix} [SQN] Started {indexPrefix} \"{currentTargetName}\" - {phaseName} Phase");
			Console.WriteLine();

			try
			{
				Process sevenzip = new();
				sevenzip.StartInfo.FileName = config.get7zExecutable(phaseName);
				sevenzip.StartInfo.WorkingDirectory = $"{(config.IncludeRootDirectory ? Utils.extractSuperDirectoryName(path) : path)}\\";
				sevenzip.StartInfo.Arguments = $"{config.CommonArguments} {phaseParameter} {extraParameters}";
				sevenzip.StartInfo.UseShellExecute = false;
				sevenzip.StartInfo.RedirectStandardOutput = true;
				sevenzip.StartInfo.RedirectStandardError = true;

				sevenzip.Start();

				string outFileNameFormatted = string.Format(SEVENZIP_STDOUT_LOG_NAME, dateTime, sevenzip.Id, phaseName, currentTargetName);
				string errFileNameFormatted = string.Format(SEVENZIP_STDERR_LOG_NAME, dateTime, sevenzip.Id, phaseName, currentTargetName);
				string outFilePath = $"{currentExecutablePath}{logFileDirectoryName}\\{outFileNameFormatted}";
				string errFilePath = $"{currentExecutablePath}{logFileDirectoryName}\\{errFileNameFormatted}";

				// Redirect STDOUT, STDERR
				(StringBuilder stdoutBuffer, StringBuilder stderrBuffer) = attachLogBuffer(sevenzip, true);
				sevenzip.BeginOutputReadLine();
				sevenzip.BeginErrorReadLine();

				sevenzip.WaitForExit();

				// Write log buffer to the file and print the path
				try
				{
					if (writeLog(outFilePath, stdoutBuffer))
						Console.WriteLine($"{indexPrefix} [SQN] STDOUT log: \"{outFilePath}\"");
					if (writeLog(errFilePath, stderrBuffer))
						Console.WriteLine($"{indexPrefix} [SQN] STDERR log: \"{errFilePath}\"");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"{indexPrefix} [SQN] Failed to write log 7z log files: {ex}");
				}

				errorCode = sevenzip.ExitCode;
			}
			catch (Exception ex)
			{
				Console.WriteLine();
				Console.WriteLine($"{indexPrefix} [SQN] Exception while executing 7z: {ex}");
			}

			bool error = errorCode != 0;

			if (error)
			{
				Console.WriteLine();
				Console.Title = $"{indexPrefix} [SQN] Error compressing \"{path}\" - {phaseName} phase";
				Utils.printError("SQN", $"7z process exited with errors/warnings. Error code: {errorCode} ({Utils.get7ZipExitCodeInformation(errorCode)}). Check the following log files for more detailed informations.");
			}

			Console.WriteLine();
			Utils.printConsoleAndTitle($"{indexPrefix} [SQN] \"{currentTargetName}\" - {phaseName} Phase Finished.");

			return error;
		}

		private static bool writeLog(string path, StringBuilder sb)
		{
			if (sb.Length > 0)
			{
				var stream = new StreamWriter(path, UTF_8_WITHOUT_BOM, LOG_FILE_STREAM_OPTIONS);
				stream.Write(sb.ToString());
				stream.Close();

				return true;
			}

			return false;
		}

		// wtf formatter?
		private static DataReceivedEventHandler getBufferRedirectHandler(StringBuilder buffer, bool alsoConsole) => new((_, param) =>
																																		   {
																																			   string? data = param.Data;
																																			   if (!string.IsNullOrEmpty(data))
																																			   {
																																				   buffer.AppendLine(data);
																																				   if (alsoConsole)
																																					   Console.WriteLine("[7z] " + data);
																																			   }
																																		   });

		private static (StringBuilder, StringBuilder) attachLogBuffer(Process process, bool alsoConsole)
		{
			var stdoutBuffer = new StringBuilder();
			var stderrBuffer = new StringBuilder();
			process.OutputDataReceived += getBufferRedirectHandler(stdoutBuffer, alsoConsole);
			process.ErrorDataReceived += getBufferRedirectHandler(stderrBuffer, alsoConsole);
			return (stdoutBuffer, stderrBuffer);
		}
	}
}
