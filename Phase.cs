﻿using System.Diagnostics;
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

		public void Initialize(string currentExecutablePath, Config config, string logFileDirectoryName)
		{
			this.currentExecutablePath = currentExecutablePath;
			this.config = config;
			this.logFileDirectoryName = logFileDirectoryName;
			phaseParameter = config.getPhaseSpecificParameters(phaseName);
		}

		public async Task ReadFileList()
		{
			if (isTerminal)
				return;

			const string _namespace = nameof(ReadFileList);
			string fileListPath = $"{currentExecutablePath}{phaseName}{FILELIST_SUFFIX}";
			if (File.Exists(fileListPath))
			{
				Utils.PrintConsole($"Reading file list: \"{fileListPath}\"", _namespace);

				try
				{
					string[] lines = await File.ReadAllLinesAsync(fileListPath);
					static string DropLeadingPathSeparators(string trimmed)
					{
						Utils.TrimLeadingPathSeparators(ref trimmed);
						return trimmed;
					}
					filterElements = (from line in lines
									  let commentRemoved = line.Contains("//") ? line[..line.IndexOf("//")] : line
									  where !string.IsNullOrWhiteSpace(commentRemoved)
									  select DropLeadingPathSeparators(commentRemoved.Trim())).ToArray();
				}
				catch (Exception ex)
				{
					Utils.PrintConsole($"Error reading file list: {ex}", _namespace);
				}
			}
			else
			{
				Utils.PrintConsole($"Phase filter file not found for phase: {fileListPath}", _namespace);
			}
		}

		public string? RebuildFileList(string path, string fileNamePrefix, EnumerationOptions recursiveEnumeratorOptions, ref HashSet<string>? availableFilesForThisTarget)
		{
			if (isTerminal || config == null || filterElements == null)
				return null;

			const string _namespace = nameof(RebuildFileList);
			bool includeRoot = config.IncludeRootDirectory;
			string targetDirectoryName = Utils.ExtractTargetName(path);

			var newFilterElements = new List<string>();
			HashSet<string>? availableFilesForThisTarget_ = availableFilesForThisTarget;
			Parallel.ForEach(filterElements, filter =>
			{
				try
				{
					if (!filter.Contains('\\') || Directory.Exists(path + '\\' + Utils.ExtractSuperDirectoryName(filter)))
					{
						IEnumerable<string> files = Directory.EnumerateFiles(path, filter, recursiveEnumeratorOptions);
						if (files.Any())
						{
							Utils.PrintConsole($"Found files for filter \"{filter}\"", _namespace);
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
					Utils.PrintConsole($"Error re-building file list: {ex}", _namespace);
				}
			});

			string? fileListPath = null;

			if (newFilterElements.Count > 0)
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

		public async Task<bool> PerformPhaseParallel(string path, string extraParameters)
		{
			if (config == null)
				return false;

			string currentTargetName = Utils.ExtractTargetName(path);
			string _namespace = $"{nameof(PerformPhaseParallel)}-{phaseName}(\"{currentTargetName}\")";
			DateTime dateTime = DateTime.Now;

			try
			{
				Process sevenzip = new();
				sevenzip.StartInfo.FileName = config.get7zExecutable(phaseName);
				sevenzip.StartInfo.WorkingDirectory = $"{(config.IncludeRootDirectory ? Utils.ExtractSuperDirectoryName(path) : path)}\\";
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
				(StringBuilder stdoutBuffer, StringBuilder stderrBuffer) = AttachLogBuffer(sevenzip, false);
				sevenzip.BeginOutputReadLine();
				sevenzip.BeginErrorReadLine();

				await sevenzip.WaitForExitAsync();

				int errorCode = sevenzip.ExitCode;
				if (errorCode != 0)
				{
					Console.WriteLine();
					Utils.PrintConsole($"7z process exited with errors/warnings. Error code: {errorCode} - {Utils.Get7ZipExitCodeInformation(errorCode)}. Check the following log files for more detailed informations.", _namespace);
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
				Utils.PrintConsole($"Exception while executing 7z in parallel: {ex}", _namespace);
				return false;
			}
		}

		public async Task<bool> PerformPhaseSequential(string path, string indexPrefix, string extraParameters)
		{
			if (config == null)
				return false;

			const string _namespace = nameof(PerformPhaseSequential);
			string currentTargetName = Utils.ExtractTargetName(path);
			int errorCode = -1;
			DateTime dateTime = DateTime.Now;

			Console.WriteLine($">> ===== -----<< {phaseName} Phase (Sequential) >>----- ===== <<");
			Utils.PrintConsoleAndTitle($"{indexPrefix} Started {indexPrefix} \"{currentTargetName}\" - {phaseName} Phase", _namespace);
			Console.WriteLine();

			try
			{
				Process sevenzip = new();
				sevenzip.StartInfo.FileName = config.get7zExecutable(phaseName);
				sevenzip.StartInfo.WorkingDirectory = $"{(config.IncludeRootDirectory ? Utils.ExtractSuperDirectoryName(path) : path)}\\";
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
					Utils.PrintConsole($"{indexPrefix} Failed to write log 7z log files: {ex}", _namespace);
				}

				errorCode = sevenzip.ExitCode;
			}
			catch (Exception ex)
			{
				Console.WriteLine();
				Utils.PrintConsole($"{indexPrefix} Exception while executing 7z: {ex}", _namespace);
			}

			bool error = errorCode != 0;

			if (error)
			{
				Console.WriteLine();
				Console.Title = $"{_namespace} {indexPrefix} Error compressing \"{path}\" - {phaseName} phase";
				Utils.PrintError(_namespace, $"7z process exited with errors/warnings. Error code: {errorCode} ({Utils.Get7ZipExitCodeInformation(errorCode)}). Check the following log files for more detailed informations.");
			}

			Console.WriteLine();
			Utils.PrintConsoleAndTitle($"{indexPrefix} \"{currentTargetName}\" - {phaseName} Phase Finished.", _namespace);

			return error;
		}

		private static async Task<bool> Write7zLog(string path, StringBuilder sb, string? displayName = null, string? _namespace = null)
		{
			if (sb.Length > 0)
			{
				using (var stream = new StreamWriter(path, UTF_8_WITHOUT_BOM, LOG_FILE_STREAM_OPTIONS))
					await stream.WriteAsync(sb.ToString());

				if (displayName != null)
					Utils.PrintConsole($"{displayName} log: \"{path}\"", _namespace);

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
