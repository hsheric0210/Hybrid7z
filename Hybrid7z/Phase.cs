using System.Diagnostics;

namespace Hybrid7z
{
	public class Phase
	{
		public const string FILELIST_SUFFIX = ".txt";
		public const string REBUILDED_FILELIST_SUFFIX = ".lst";

		public readonly string phaseName;
		public readonly bool isTerminal;
		public readonly bool doesntSupportMultiThread;

		public string? currentExecutablePath;
		public string? phaseParameter;
		public Config? config;

		public string[]? filterElements;

		public Phase(string phaseName, bool isTerminal, bool doesntSupportMultiThread)
		{
			this.phaseName = phaseName;
			this.isTerminal = isTerminal;
			this.doesntSupportMultiThread = doesntSupportMultiThread;
		}

		public void Init(string currentExecutablePath, Config config)
		{
			this.currentExecutablePath = currentExecutablePath;
			this.config = config;
			phaseParameter = config.GetPhaseSpecificParameters(phaseName);
		}

		public Task? ReadFileList()
		{
			if (isTerminal)
				return null;

			string filelistPath = currentExecutablePath + phaseName + FILELIST_SUFFIX;
			if (File.Exists(filelistPath))
			{
				Utils.PrintConsoleAndTitle($"[RFL] Reading file list: \"{filelistPath}\"");

				try
				{
					return File.ReadAllLinesAsync(filelistPath).ContinueWith(task =>
					{
						var strings = new List<string>();
						foreach (string line in task.Result)
						{
							string commentRemoved = line.Contains("//") ? line[..line.IndexOf("//")] : line;
							if (!string.IsNullOrWhiteSpace(commentRemoved))
							{
								Console.WriteLine($"[RFL] Readed from {filelistPath}: \"{commentRemoved}\"");
								strings.Add(commentRemoved.Trim());
							}
						}
						filterElements = strings.ToArray();
					});
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[RFL] Error reading file list: {ex}");
				}
			}
			else
				Console.WriteLine($"[RFL] Phase filter file not found for phase: {filelistPath}");

			return null;
		}

		public Task<string?> RebuildFileList(string path, string fileNamePrefix)
		{
			if (isTerminal || config == null || filterElements == null)
				return Task.FromResult((string?)null);

			bool includeRoot = config.IncludeRootDirectory;
			string targetDirectoryName = Utils.ExtractTargetName(path);

			return Task.Run(() =>
			{
				var newFilterElements = new List<string>();
				var tasks = new List<Task>();
				foreach (string? filter in filterElements)
					tasks.Add(Task.Run(() =>
					{
						try
						{
							if (Directory.EnumerateFiles(path, filter, SearchOption.AllDirectories).Any())
							{
								Console.WriteLine($"[RbFL] Found files for filter \"{filter}\"");
								newFilterElements.Add((includeRoot ? targetDirectoryName + "\\" : "") + filter);
							}
						}
						catch (Exception ex)
						{
							Console.WriteLine($"[RbFL] Error re-building file list: {ex}");
						}
					}));

				Task.WhenAll(tasks.ToArray()).Wait();

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
			});
		}

		public Task? PerformPhaseParallel(string path, string extraParameters)
		{
			if (config == null)
				return null;

			string currentTargetName = Utils.ExtractTargetName(path);

			Console.WriteLine($">> ===== ----- {phaseName} Phase (Parallel) ----- ===== <<");
			Utils.PrintConsoleAndTitle($"[PRL-{phaseName}] Queued \"{currentTargetName}\" - {phaseName} Phase");
			Console.WriteLine();

			Task? task = null;

			try
			{
				Process sevenzip = new();
				sevenzip.StartInfo.FileName = config.SevenZipExecutable;
				sevenzip.StartInfo.WorkingDirectory = $"{(config.IncludeRootDirectory ? Utils.ExtractSuperDirectoryName(path) : path)}\\";
				sevenzip.StartInfo.Arguments = $"{config.CommonArguments} {phaseParameter} {extraParameters}";
				sevenzip.StartInfo.UseShellExecute = true;

				Console.WriteLine($"[PRL-{phaseName}] Params: {sevenzip.StartInfo.Arguments}");

				sevenzip.Start();

				task = sevenzip.WaitForExitAsync().ContinueWith((task) =>
				{
					int errorCode = sevenzip.ExitCode;
					if (errorCode != 0)
					{
						Console.WriteLine();
						Console.WriteLine($"[PRL-{phaseName}] 7z process exited with errors/warnings. Error code {errorCode} ({Utils.Get7ZipExitCodeInformation(errorCode)})");
					}
				});
			}
			catch (Exception ex)
			{
				Console.WriteLine();
				Console.WriteLine($"[PRL-{phaseName}] Exception while executing 7z in parallel: {ex}");
			}

			return task;
		}

		public bool PerformPhaseSequential(string path, string indexPrefix, string extraParameters)
		{
			if (config == null)
				return false;

			string currentTargetName = Utils.ExtractTargetName(path);

			Console.WriteLine($">> ===== -----<< {phaseName} Phase (Sequential) >>----- ===== <<");
			Utils.PrintConsoleAndTitle($"{indexPrefix} [SQN] Started {indexPrefix} \"{currentTargetName}\" - {phaseName} Phase");
			Console.WriteLine();

			int errorCode = -1;

			try
			{
				Process sevenzip = new();
				sevenzip.StartInfo.FileName = config.SevenZipExecutable;
				sevenzip.StartInfo.WorkingDirectory = $"{(config.IncludeRootDirectory ? Utils.ExtractSuperDirectoryName(path) : path)}\\";
				sevenzip.StartInfo.Arguments = $"{config.CommonArguments} {phaseParameter} {extraParameters}";
				sevenzip.StartInfo.UseShellExecute = false;

				Console.WriteLine($"{indexPrefix} [SQN] Params: {sevenzip.StartInfo.Arguments}");

				sevenzip.Start();
				sevenzip.WaitForExit();
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
				Utils.PrintError("SQN", $"7z process exited with errors/warnings. Error code {errorCode} ({Utils.Get7ZipExitCodeInformation(errorCode)})");
			}

			Console.WriteLine();
			Utils.PrintConsoleAndTitle($"{indexPrefix} [SQN] \"{currentTargetName}\" - {phaseName} Phase Finished.");

			return error;
		}
	}
}
