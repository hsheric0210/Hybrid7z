using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hybrid7z.Phase;
internal class Archiver
{
	private const string StdoutLogName = "{0:yyyy-MM-dd_HH-mm-ss-FFFFFF}-{1}_{2}({3})-STDOUT.log";
	private const string StderrLogName = "{0:yyyy-MM-dd_HH-mm-ss-FFFFFF}-{1}_{2}({3})-STDERR.log";

	private static readonly UTF8Encoding Utf8WithoutBom = new(false);
	private static readonly FileStreamOptions LogFileStreamOptions = new()
	{
		Access = FileAccess.Write,
		Mode = FileMode.Create
	};

	private readonly Config config;
	private readonly string phaseName;
	private readonly string targetPath;
	private readonly string logFolder;

	public Archiver(Config config, string phaseName, string targetPath, string logFolder)
	{
		this.config = config;
		this.phaseName = phaseName;
		this.targetPath = targetPath;
		this.logFolder = logFolder;
	}

	private Process SetupExecution(string extraParameters)
	{
		Process archiver = new();
		archiver.StartInfo.FileName = config.Get7zExecutable(phaseName);
		archiver.StartInfo.WorkingDirectory = Path.GetFullPath(config.IncludeRootDirectory ? PathUtils.GetParentDirectory(targetPath) : targetPath) + Path.DirectorySeparatorChar;
		archiver.StartInfo.Arguments = $"{config.GlobalArchiverParameters} {config.GetPhaseSpecificParameters(phaseName)} {extraParameters}";
		archiver.StartInfo.UseShellExecute = false;
		archiver.StartInfo.RedirectStandardOutput = true;
		archiver.StartInfo.RedirectStandardError = true;

		Log.Verbose(
			"Running archiver {executable} with working directory {cwd} with arguments {arguments}",
			archiver.StartInfo.FileName,
			archiver.StartInfo.WorkingDirectory,
			archiver.StartInfo.Arguments);
		return archiver;
	}

	public async Task<int> Execute(string extraParameters)
	{
		DateTime dateTime = DateTime.Now;
		var nameOfTarget = Path.GetFileName(targetPath);

		Process archiver = SetupExecution(extraParameters);
		archiver.Start();

		if (!new DirectoryInfo(logFolder).Exists)
			Directory.CreateDirectory(logFolder);

		var outFilePath = Path.Combine(logFolder, string.Format(StdoutLogName, dateTime, archiver.Id, phaseName, nameOfTarget));
		var errFilePath = Path.Combine(logFolder, string.Format(StderrLogName, dateTime, archiver.Id, phaseName, nameOfTarget));
		(StringBuilder stdoutBuffer, StringBuilder stderrBuffer) = AttachLogBuffer(archiver, true);

		archiver.BeginOutputReadLine();
		archiver.BeginErrorReadLine();

		await archiver.WaitForExitAsync();

		try
		{
			var logTasks = new Task<bool>[2];
			logTasks[0] = WriteStreamLog(outFilePath, stdoutBuffer, "STDOUT");
			logTasks[1] = WriteStreamLog(errFilePath, stderrBuffer, "STDERR");
			await Task.WhenAll(logTasks);
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Exception during writing archiver log files.");
		}

		return archiver.ExitCode;
	}

	private async Task<bool> WriteStreamLog(string path, StringBuilder sb, string? displayName = null)
	{
		if (sb.Length > 0)
		{
			using (var stream = new StreamWriter(path, Utf8WithoutBom, LogFileStreamOptions))
				await stream.WriteAsync(sb.ToString());

			if (displayName != null)
				Log.Debug($"[{displayName}] \"{path}\"");

			return true;
		}

		return false;
	}

	private static DataReceivedEventHandler GetBufferReceiver(string type, StringBuilder buffer, bool alsoConsole)
	{
		return new((_, param) =>
		{
			var data = param.Data;
			if (!string.IsNullOrEmpty(data))
			{
				buffer.AppendLine(data);
				if (alsoConsole)
					Console.WriteLine($"[{type}] {data}");
			}
		});
	}

	private static (StringBuilder, StringBuilder) AttachLogBuffer(Process process, bool alsoConsole)
	{
		var stdoutBuffer = new StringBuilder();
		var stderrBuffer = new StringBuilder();
		process.OutputDataReceived += GetBufferReceiver("STDOUT", stdoutBuffer, alsoConsole);
		process.ErrorDataReceived += GetBufferReceiver("STDERR", stderrBuffer, alsoConsole);
		return (stdoutBuffer, stderrBuffer);
	}
}
