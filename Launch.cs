using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace Hybrid7z;
public struct LaunchParameters
{
	public LaunchParameters()
	{
	}

	public string? ConfigFile { get; set; } = null;
	public string? LogFolder { get; set; } = null;
	public string? BatchFile { get; set; } = null;
	public string? PhaseFilterFolder { get; set; } = null;
}

public class Launch
{
	public const string VERSION = "0.11";
	public const string DefaultLogFileName = "Hybrid7z.log";
	public const string DefaultConfigFileName = "Hybrid7z.toml";
	public const string DefaultLogFolder = "logs";

	public static void Main(params string[] args)
	{
		var cwd = AppDomain.CurrentDomain.BaseDirectory;
		try
		{
			Log.Logger = new LoggerConfiguration()
				.MinimumLevel.Verbose()
				.WriteTo.Async(a => a.File(
					Path.Combine(cwd, DefaultLogFileName),
					fileSizeLimitBytes: 268435456,
					buffered: true,
					flushToDiskInterval: TimeSpan.FromSeconds(2),
					rollOnFileSizeLimit: true))
				.WriteTo.Console(theme: AnsiConsoleTheme.Code)
				.CreateLogger();
		}
		catch (Exception ex)
		{
			Console.WriteLine("Logger creation failure.");
			Console.WriteLine(ex);
		}

		Log.Information($"Hybrid7z v{VERSION} started on {{date}}", DateTime.Now);

		try
		{
			var switchIndex = ParseSwitches(args, out LaunchParameters options);
			if (switchIndex < 0)
				return;

			// Check configuration file is exists
			var configPath = options.ConfigFile ?? cwd + DefaultConfigFileName;
			if (!File.Exists(configPath))
			{
				Log.Warning("Configuration file not found! Writing default configuration file to: {file}", configPath);
				Config.SaveDefaults(configPath);
			}
			// Start the program

			new Instance().Start(cwd, args.Skip(switchIndex).ToList(), options).Wait();
		}
		catch (Exception ex)
		{
			Log.Fatal(ex, "Unhandled exception caught! Please report to the author.");
		}
		Log.CloseAndFlush();
	}

	private static int ParseSwitches(string[] parameters, out LaunchParameters options)
	{
		options = new LaunchParameters();
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
						// help
						case "help":
						case "?":
						case "h":
						{
							Console.WriteLine("Hybrid7z.exe <switches> <target folders>");
							Console.WriteLine("Note that 'Hybrid7z.exe <target folders> <switches>' is not working");
							Console.WriteLine("Switch prefix '-', '/' is all supported: '-help' and '/help' do the same thing");
							Console.WriteLine("Available commands:");
							Console.WriteLine("\t/?, /h, /help\t\t\t\t\tPrint the usages");
							Console.WriteLine("\t/c, /cfg, /configFile\t\t<fileName>\tSpecify the configuration file)");
							Console.WriteLine("\t/log, /logDir, /logRoot\t\t<dirName>\tSpecify the directory to save the log files (non-existent directory will be created)");
							Console.WriteLine("\t/b, /batch, /batchFile\t\t<fileName>\tSpecify the batch file, written in this format: <source>|<destination>|<password>)");
							Console.WriteLine("\t/y, /yesall, /nobreak, /nopause\t\t\tNo-pause mode: Skip all pause points");
							return -1;
						}

						case "c":
						case "cfg":
						case "configfile":
						{
							if (parameters.Length > switchIndex + 1 && new FileInfo(parameters[switchIndex + 1]).Exists)
								options.ConfigFile = parameters[switchIndex + 1];
							else
							{
								Log.Warning("You must specify the existing config file (ex: '-configfile hybrid7z.toml')");
							}
							switchIndex++; // Skip trailing input file name part
							break;
						}

						case "log":
						case "logdir":
						case "logroot":
						case "logfolder":
						{
							if (parameters.Length > switchIndex + 1)
								options.LogFolder = parameters[switchIndex + 1];
							else
								Log.Warning("You must specify the log root folder after the logRoot switch (ex: '-logroot logs')");
							switchIndex++; // Skip trailing directory name part
							break;
						}

						// batchfile
						case "b":
						case "batch":
						case "batchfile":
						{
							if (parameters.Length > switchIndex + 1 && new FileInfo(parameters[switchIndex + 1]).Exists)
								options.BatchFile = parameters[switchIndex + 1];
							else
							{
								Log.Warning("You must specify the existing input file (ex: '-batchfile batch.txt')");
								Log.Warning("The input file must follow these format: <targetName>|<archiveName>|<archivePassword> (archivePassword may be empty)");
							}
							switchIndex++; // Skip trailing input file name part
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

}
