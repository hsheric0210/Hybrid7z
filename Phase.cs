using Serilog;
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

		private string? cwd;
		private string? PhaseParameter;
		private Config? Config;
		private string? LogFileDirectoryName;
		private string[]? FilterElements;

		public Phase(string phaseName, bool isTerminal, bool doesntSupportMultiThread)
		{
			this.phaseName = phaseName;
			this.isTerminal = isTerminal;
			this.doesntSupportMultiThread = doesntSupportMultiThread;
		}

		public void Initialize(string cwd, Config config, string logFileDirectoryName)
		{
			this.cwd = cwd;
			this.Config = config;
			this.LogFileDirectoryName = logFileDirectoryName;
			PhaseParameter = config.GetPhaseSpecificParameters(phaseName);
		}

		public async Task ParseFileList()
		{
			if (isTerminal)
				return;

			var filterPath = cwd + phaseName + FILELIST_SUFFIX;
			if (File.Exists(filterPath))
			{
				Log.Information("Parsing file list: {path}", filterPath);

				try
				{
					var lines = await File.ReadAllLinesAsync(filterPath);
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
					Log.Error(ex, "Error reading phase filter file: {path}", filterPath);
				}
			}
			else
			{
				Log.Warning("Phase filter file not found: {path}", filterPath);
			}
		}

		public string? RebuildFilter(string path, string fileNamePrefix, EnumerationOptions recursiveEnumeratorOptions, ref HashSet<string> availableFilesForThisTarget)
		{
			if (isTerminal || Config == null || FilterElements == null)
				return null;

			var includeRoot = Config.IncludeRootDirectory;
			var targetDirectoryName = Utils.ExtractTargetName(path);

			var newFilterElements = new List<string>();
			HashSet<string>? availableFilesForThisTarget_ = availableFilesForThisTarget; // FIXME: 보아하니 Hybrid7z.cs#112 의 fixme가 불가능한 게 여기서 그대로 Closure 안으로 ref 파라미터를 넘길 수 없어서 그런 것 같군. 그러면 record 하나 만든 후 거기에 집어넣어서 넘기면 되는거 아님?
			Parallel.ForEach(FilterElements, filter =>
			{
				try
				{
					if (!filter.Contains('\\') || Directory.Exists(path + '\\' + Utils.ExtractSuperDirectoryName(filter)))
					{
						IEnumerable<string> files = Directory.EnumerateFiles(path, filter, recursiveEnumeratorOptions);
						if (files.Any())
						{
							Log.Information("Found files for filter: filter={filter}, path={path}", filter, path);
							newFilterElements.Add((includeRoot ? targetDirectoryName + "\\" : "") + filter);

							if (availableFilesForThisTarget_ != null)
							{
								lock (availableFilesForThisTarget_)
								{
									foreach (var filepath in files)
										availableFilesForThisTarget_.Remove(filepath.ToUpperInvariant()); // Very inefficient solution; But, at least, hey, it iss working!
								}
							}
						}
					}
				}
				catch (Exception ex)
				{
					Log.Error(ex, "Exception during phase filter rebuilding: {path}", path);
				}
			});

			string? filterPath = null;

			if (newFilterElements.Count > 0)
			{
				filterPath = cwd + fileNamePrefix + phaseName + REBUILDED_FILELIST_SUFFIX;
				try
				{
					File.WriteAllLines(filterPath, newFilterElements);
				}
				catch (Exception ex)
				{
					Log.Error(ex, "Exception during phase filter rewriting: {path}", filterPath);
				}
			}

			return filterPath;
		}

		public async Task<bool> PerformPhaseParallel(string path, string extraParameters)
		{
			if (Config == null)
				return false;

			var currentTargetName = Utils.ExtractTargetName(path);
			var _namespace = $"{nameof(PerformPhaseParallel)}-{phaseName}(\"{currentTargetName}\")";
			DateTime dateTime = DateTime.Now;

			try
			{
				Process sevenzip = new();
				sevenzip.StartInfo.FileName = Config.Get7zExecutable(phaseName);
				sevenzip.StartInfo.WorkingDirectory = $"{(Config.IncludeRootDirectory ? Utils.ExtractSuperDirectoryName(path) : path)}\\";
				sevenzip.StartInfo.Arguments = $"{Config.GlobalArchiverParameters} {PhaseParameter} {extraParameters}";
				sevenzip.StartInfo.UseShellExecute = false;
				sevenzip.StartInfo.RedirectStandardOutput = true;
				sevenzip.StartInfo.RedirectStandardError = true;

				sevenzip.Start();

				var outFileNameFormatted = string.Format(SEVENZIP_STDOUT_LOG_NAME, dateTime, sevenzip.Id, phaseName, currentTargetName);
				var errFileNameFormatted = string.Format(SEVENZIP_STDERR_LOG_NAME, dateTime, sevenzip.Id, phaseName, currentTargetName);
				var outFilePath = $"{cwd}{LogFileDirectoryName}\\{outFileNameFormatted}";
				var errFilePath = $"{cwd}{LogFileDirectoryName}\\{errFileNameFormatted}";

				// Redirect STDOUT, STDERR
				(StringBuilder stdoutBuffer, StringBuilder stderrBuffer) = AttachLogBuffer(sevenzip, false);
				sevenzip.BeginOutputReadLine();
				sevenzip.BeginErrorReadLine();

				await sevenzip.WaitForExitAsync();
				Console.WriteLine();

				var errorCode = sevenzip.ExitCode;
				if (errorCode != 0)
				{
					Log.Warning("Archiver process exited with non-zero exit code: {code}", errorCode);
					Log.Warning("Exit code details: {info}", Utils.Get7ZipExitCodeInformation(errorCode));
				}

				var logTasks = new Task<bool>[2];
				logTasks[0] = Write7zLog(outFilePath, stdoutBuffer, "STDOUT");
				logTasks[1] = Write7zLog(errFilePath, stderrBuffer, "STDERR");
				await Task.WhenAll(logTasks);

				return errorCode == 0;
			}
			catch (Exception ex)
			{
				Log.Error("Exception during archiver parallel execution", ex);
				return false;
			}
		}

		public async Task<bool> PerformPhaseSequential(string path, string indexPrefix, string extraParameters)
		{
			if (Config == null)
				return false;

			var currentTargetName = Utils.ExtractTargetName(path);
			var errorCode = -1;
			DateTime dateTime = DateTime.Now;

			Console.WriteLine($">> ===== -----<< {phaseName} Phase (Sequential) >>----- ===== <<");
			Log.Information("+++ {prefix} Phase {phase} sequential execution: {target}", indexPrefix, phaseName, path);
			Console.WriteLine();

			try
			{
				Process sevenzip = new();
				sevenzip.StartInfo.FileName = Config.Get7zExecutable(phaseName);
				sevenzip.StartInfo.WorkingDirectory = $"{(Config.IncludeRootDirectory ? Utils.ExtractSuperDirectoryName(path) : path)}\\";
				sevenzip.StartInfo.Arguments = $"{Config.GlobalArchiverParameters} {PhaseParameter} {extraParameters}";
				sevenzip.StartInfo.UseShellExecute = false;
				sevenzip.StartInfo.RedirectStandardOutput = true;
				sevenzip.StartInfo.RedirectStandardError = true;

				sevenzip.Start();

				var outFileNameFormatted = string.Format(SEVENZIP_STDOUT_LOG_NAME, dateTime, sevenzip.Id, phaseName, currentTargetName);
				var errFileNameFormatted = string.Format(SEVENZIP_STDERR_LOG_NAME, dateTime, sevenzip.Id, phaseName, currentTargetName);
				var outFilePath = $"{cwd}{LogFileDirectoryName}\\{outFileNameFormatted}";
				var errFilePath = $"{cwd}{LogFileDirectoryName}\\{errFileNameFormatted}";

				// Redirect STDOUT, STDERR
				(StringBuilder stdoutBuffer, StringBuilder stderrBuffer) = AttachLogBuffer(sevenzip, true);
				sevenzip.BeginOutputReadLine();
				sevenzip.BeginErrorReadLine();

				await sevenzip.WaitForExitAsync();

				// Write log buffer to the file and print the path
				try
				{
					var logTasks = new Task<bool>[2];
					logTasks[0] = Write7zLog(outFilePath, stdoutBuffer, "STDOUT");
					logTasks[1] = Write7zLog(errFilePath, stderrBuffer, "STDERR");
					await Task.WhenAll(logTasks);
				}
				catch (Exception ex)
				{
					Log.Error(ex, "{prefix} Failed to write log 7z log files");
				}

				errorCode = sevenzip.ExitCode;
			}
			catch (Exception ex)
			{
				Console.WriteLine();
				Log.Error(ex, "{prefix} Exception during archiver sequential execution.", indexPrefix);
			}

			var error = errorCode != 0;

			if (error)
			{
				Console.WriteLine();
				Console.Title = $"{indexPrefix} Error compressing \"{path}\" - {phaseName} phase";
				Log.Warning("Archiver process exited with non-zero exit code: {code}", errorCode);
				Log.Warning("Exit code details: {info}", Utils.Get7ZipExitCodeInformation(errorCode));
			}

			Console.WriteLine();
			Log.Debug($"{indexPrefix} \"{currentTargetName}\" - {phaseName} Phase Finished.");

			return error;
		}

		private async Task<bool> Write7zLog(string path, StringBuilder sb, string? displayName = null)
		{
			if (sb.Length > 0)
			{
				using (var stream = new StreamWriter(path, UTF_8_WITHOUT_BOM, LOG_FILE_STREAM_OPTIONS))
					await stream.WriteAsync(sb.ToString());

				if (displayName != null)
					Log.Debug($"[{displayName}] \"{path}\"");

				return true;
			}

			return false;
		}

		private static DataReceivedEventHandler GetBufferRedirectHandler(StringBuilder buffer, bool alsoConsole)
		{
			return new((_, param) =>
				{
					var data = param.Data;
					if (!string.IsNullOrEmpty(data))
					{
						buffer.AppendLine(data);
						if (alsoConsole)
							Console.WriteLine("[7z] " + data);
					}
				});
		}

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
