using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;

namespace Hybrid7z
{
	public class Program
	{
		public const string VERSION = "0.1";

		public readonly Phase[] phases;
		public readonly string currentExecutablePath;
		private readonly Config config;

		public List<string> rebuildedFileLists = new List<string>();

		public bool anyErrors;

		public static void Main(string[] args)
		{
			Console.WriteLine($"Hybrid7z v{VERSION} - A hybrid 7-zip compressor");

			string currentExecutablePath = AppDomain.CurrentDomain.BaseDirectory;

			// Check configuration file is exists
			if (!File.Exists(currentExecutablePath + "Hybrid7z.ini"))
			{
				Console.WriteLine("Writing default config");
				SaveDefaultConfig(currentExecutablePath);
			}

			// Start the program
			new Program(currentExecutablePath, args);
		}

		public Program(string currentExecutablePath, string[] param)
		{
			this.currentExecutablePath = currentExecutablePath;

			config = new Config(new IniFile($"{currentExecutablePath}Hybrid7z.ini"));

			phases = new Phase[5];
			phases[0] = new Phase("PPMd", false, true);
			phases[1] = new Phase("Copy", false, true);
			phases[2] = new Phase("LZMA2", false, false);
			phases[3] = new Phase("x86", false, false);
			phases[4] = new Phase("FastLZMA2", true, false);

			foreach (var phase in phases)
			{
				PrintConsoleAndTitle($"Initializing phase: {phase.phaseName}");
				phase.Init(currentExecutablePath, config);
				phase.ReadFileList();
			}

			int totalFileCount = param.Length;
			var targets = new List<string>();

			foreach (string targetPath in param)
			{
				// string titlePrefix = $"[{currentFileIndex}/{totalFileCount}]";

				if (Directory.Exists(targetPath))
					targets.Add(targetPath);
				// anyErrors = ProcessDirectory(filename, titlePrefix) || anyErrors;
				else if (File.Exists(targetPath))
					Console.WriteLine($"WARNING: Currently, file are not supported (only directories are supported) - \"{targetPath}\"");
				else
					Console.WriteLine($"WARNING: File not exists - \"{targetPath}\"");
			}

			var multithreadedPhases = new List<Phase>();
			foreach (var phase in phases)
			{
				if (phase.isSingleThreaded)
					anyErrors = RunSinglethreadedPhase(phase, targets) || anyErrors;
				else
					multithreadedPhases.Add(phase);
			}

			int currentFileIndex = 1;
			foreach (var target in targets)
			{
				string titlePrefix = $"[{currentFileIndex}/{totalFileCount}]";
				anyErrors = RunMultithreadedPhase(multithreadedPhases, target, titlePrefix) || anyErrors;
				currentFileIndex++;
			}

			// Print process result
			if (anyErrors)
			{
				Console.WriteLine("One or more file(s) failed to process");
				Console.BackgroundColor = ConsoleColor.DarkRed;
			}
			else
			{
				Console.WriteLine("All files are successfully proceed without any error(s).");
				Console.BackgroundColor = ConsoleColor.DarkBlue;
			}
			Console.WriteLine("Press any key to exit program...");
			Console.ReadKey();

			// Delete any left-over filelist files
			foreach (string filelist in rebuildedFileLists)
			{
				if (File.Exists(filelist))
				{
					File.Delete(filelist);
					Console.WriteLine($"Deleted {filelist}");
				}
			}
		}

		private static void SaveDefaultConfig(string currentDir)
		{
			var ini = new IniFile($"{currentDir}Hybrid7z.ini");
			ini.Write("7z", "7z.exe");
			ini.Write("BaseArgs", "a -t7z -mhe -ms=1g -mqs -slp -bt -bb3 -sae");
			ini.Write("Args_PPMd", "-m0=PPMd -mx=9 -myx=9 -mmem=1024m -mo=32 -mmt=1");
			ini.Write("Args_LZMA2", "-m0=LZMA2 -mx=9 -myx=9 -md=256m -mfb=273 -mmt=8 -mmtf -mmf=bt4 -mmc=10000 -mlc=4");
			ini.Write("Args_Copy", "-m0=Copy -mx=0");
			ini.Write("Args_x86", "-mf=BCJ2 -m0=LZMA2 -mx=9 -myx=9 -md=1024m -mfb=273 -mmt=8 -mmtf -mmf=bt4 -mmc=10000 -mlc=4");
			ini.Write("Args_FastLZMA2", "-m0=FLZMA2 -mx=9 -myx=9 -md=1024m -mfb=273 -mmt=32 -mmtf -mlc=4");
			ini.Write("IncludeRootDirectory", "0");
		}

		public static void PrintConsoleAndTitle(string message)
		{
			Console.WriteLine(message);
			Console.Title = message;
		}

		private bool RunSinglethreadedPhase(Phase phase, IEnumerable<string> paths)
		{
			bool includeRoot = config.IncludeRootDirectory;

			bool thereIsNullTask = false;
			var taskList = new List<Task>();

			foreach (var path in paths)
			{
				string currentTargetName = path[(path.LastIndexOf('\\') + 1)..];
				string archiveFileName = $"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z";

				// var tupleArray = new (string, string, string[]?)[] { ("PPMd", PPMD_REBUILDED_LIST, ppmdList), ("LZMA2", LZMA2_REBUILDED_LIST, lzma2List), ("Copy", COPY_REBUILDED_LIST, copyList), ("x86", X86_REBUILDED_LIST, x86List) };

				Console.WriteLine("<<==================== <*> ====================>>");

				PrintConsoleAndTitle($"Re-building file list for \"{path}\"");

				string? fileListPath = phase.RebuildFileList(path, currentTargetName + ".");

				if (fileListPath != null)
				{
					PrintConsoleAndTitle($"Start processing \"{path}\"");
					Console.WriteLine();

					var task = phase.PerformPhaseAsync(path, $"-ir@\"{fileListPath}\" -- \"{archiveFileName}\"");

					if (task != null)
						taskList.Add(task);
					else
						thereIsNullTask = true;

					rebuildedFileLists.Add(fileListPath);

					Console.WriteLine();
					PrintConsoleAndTitle($"Finished processing {path}");
				}

				Console.WriteLine("<<==================== <~> <$> ====================>>");
			}

			PrintConsoleAndTitle("Waiting until all asynchronous processes are finished...");

			var tick = Environment.TickCount;

			var allTask = Task.WhenAll(taskList);
			allTask.Wait();

			Console.WriteLine($"All asynchronous processes are finished! (Took {Environment.TickCount - tick}ms)");

			return thereIsNullTask || allTask.IsFaulted;
		}

		private bool RunMultithreadedPhase(IEnumerable<Phase> phases, string path, string titlePrefix)
		{
			bool includeRoot = config.IncludeRootDirectory;
			string currentTargetName = path[(path.LastIndexOf('\\') + 1)..];
			string archiveFileName = $"{(includeRoot ? "" : "..\\")}{currentTargetName}.7z";

			bool error = false;

			// var tupleArray = new (string, string, string[]?)[] { ("PPMd", PPMD_REBUILDED_LIST, ppmdList), ("LZMA2", LZMA2_REBUILDED_LIST, lzma2List), ("Copy", COPY_REBUILDED_LIST, copyList), ("x86", X86_REBUILDED_LIST, x86List) };

			Console.WriteLine("<<=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-= <*> =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=>>");

			PrintConsoleAndTitle($"{titlePrefix} Start processing \"{path}\"");
			Console.WriteLine();

			foreach (var phase in phases)
			{
				string? fileListPath = null; // = $"{currentExecutablePath}{phase.phaseName}{Phase.rebuildedFileListSuffix}";

				if (phase.isTerminal)
					error = phase.PerformPhase(path, titlePrefix, $"-r {string.Join(" ", rebuildedFileLists.ConvertAll((from) => $"-xr@\"{from}\""))} -- \"{archiveFileName}\" \"{(includeRoot ? currentTargetName : "*")}\"") || error;
				else if (((fileListPath = phase.RebuildFileList(path)) != null))
				{
					error = phase.PerformPhase(path, titlePrefix, $"-ir@\"{fileListPath}\" -- \"{archiveFileName}\"") || error;
					rebuildedFileLists.Add(fileListPath);
				}
				else
					PrintConsoleAndTitle($"(MT) There's no files to apply {phase.phaseName} {path}");
			}

			Console.WriteLine();
			PrintConsoleAndTitle($"{titlePrefix} Finished processing {path}");

			Console.WriteLine("<<=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-= <$> <~> =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=>>");

			return error;
		}

		//private void PerformPhase(string phaseName, string path, string titlePrefix, string extraParameters)
		//{
		//	string currentTargetName = path[(path.LastIndexOf('\\') + 1)..];

		//	Console.Title = $"{titlePrefix}Compressing \"{path}\" - {phaseName} phase";

		//	Console.WriteLine("==================================");
		//	PrintConsoleAndTitle($"{titlePrefix} \"{currentTargetName}\" - {phaseName} Phase");
		//	Console.WriteLine();

		//	int errcode = -1;

		//	try
		//	{
		//		Process sevenzip = new Process();
		//		sevenzip.StartInfo.FileName = config.Read("7z");
		//		sevenzip.StartInfo.WorkingDirectory = $"{(includeRoot ? currentExecutablePath : path)}\\";
		//		sevenzip.StartInfo.Arguments = $"{config.Read("BaseArgs")} {config.Read($"Args_{phaseName}")} {extraParameters}";
		//		sevenzip.StartInfo.UseShellExecute = false;

		//		Console.WriteLine("Params: " + sevenzip.StartInfo.Arguments);

		//		sevenzip.Start();

		//		sevenzip.WaitForExit();

		//		errcode = sevenzip.ExitCode;
		//	}
		//	catch (Exception ex)
		//	{
		//		Console.WriteLine("Exception while executing 7z: " + ex.ToString());
		//	}

		//	if (errcode != 0)
		//	{
		//		Console.Title = $"{titlePrefix}Error compressing \"{path}\" - {phaseName} phase";

		//		Console.BackgroundColor = ConsoleColor.DarkRed;
		//		Console.WriteLine($"Compression finished with errors/warnings. (error code {errcode})");
		//		Console.WriteLine("Check the error message and press any key to continue.");
		//		Console.ReadKey();

		//		Console.BackgroundColor = ConsoleColor.Black;
		//		anyErrors = true;
		//	}

		//	Console.WriteLine();
		//	Console.WriteLine();
		//	PrintConsoleAndTitle($"{titlePrefix} \"{currentTargetName}\" - {phaseName} Phase Finished.");
		//}

		//private void RebuildFileList(string path, string currentDirName, (string, string, string[]?)[] fileList)
		//{

		//	foreach (var (_, filename, list) in fileList)
		//	{
		//		if (list == null) continue;

		//		var newFilterList = new List<string>();
		//		foreach (var filter in list)
		//		{
		//			try
		//			{
		//				if (Directory.EnumerateFiles(path, filter, SearchOption.AllDirectories).Any())
		//					newFilterList.Add((includeRoot ? currentDirName + "\\" : "") + filter);
		//			}
		//			catch (Exception ex)
		//			{
		//				Console.WriteLine($"Error rebuilding file list: {ex.ToString()}");
		//			}
		//		}

		//		if (newFilterList.Count > 0)
		//		{
		//			try
		//			{
		//				File.WriteAllLines(currentExecutablePath + filename, newFilterList);
		//			}
		//			catch (Exception ex)
		//			{
		//				Console.WriteLine($"Error writing rebuilded file list: {ex.ToString()}");
		//			}
		//		}
		//	}
		//}
	}

	public class Config
	{
		private readonly IniFile config;

		public string SevenZipExecutable;
		public string CommonArguments;
		public bool IncludeRootDirectory;

		public Config(IniFile config)
		{
			this.config = config;

			SevenZipExecutable = config.Read("7z");
			CommonArguments = config.Read("BaseArgs");
			IncludeRootDirectory = !String.Equals(config.Read("IncludeRootDirectory"), "0");
		}

		public string GetPhaseSpecificParameters(string phaseName)
		{
			return config.Read($"Args_{phaseName}");
		}
	}

	public class Phase
	{
		public const string fileListSuffix = ".txt";
		public const string rebuildedFileListSuffix = ".filelist.txt";

		public readonly string phaseName;
		public readonly bool isTerminal;
		public readonly bool isSingleThreaded;

		public string? currentExecutablePath;
		public Config? config;
		public string? phaseParameter;

		public string[]? filterElements;

		public Phase(string phaseName, bool isTerminal, bool isSingleThreaded)
		{
			this.phaseName = phaseName;
			this.isTerminal = isTerminal;
			this.isSingleThreaded = isSingleThreaded;
		}

		public void Init(string currentExecutablePath, Config config)
		{
			this.currentExecutablePath = currentExecutablePath;
			this.config = config;
			phaseParameter = config.GetPhaseSpecificParameters(phaseName);
		}

		public void ReadFileList()
		{
			if (isTerminal)
				return;

			string filelistPath = currentExecutablePath + phaseName + fileListSuffix;
			if (File.Exists(filelistPath))
			{
				Program.PrintConsoleAndTitle($"Reading file list: \"{filelistPath}\"");

				try
				{
					// TODO: Asynchronize
					filterElements = File.ReadAllLines(filelistPath);
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Error reading file list: {ex}");
				}
			}
			else
				Console.WriteLine($"Phase filter file not found for phase: {filelistPath}");
		}

		private void TrimTrailingPathSeparators(ref string path)
		{
			while (path.EndsWith("\\"))
				path = path[(path.LastIndexOf('\\') + 1)..];
		}

		private string ExtractTargetName(string path)
		{
			TrimTrailingPathSeparators(ref path);
			return path[(path.LastIndexOf('\\') + 1)..];
		}

		private string ExtractSuperDirectoryName(string path)
		{
			TrimTrailingPathSeparators(ref path);
			return path[..(path.LastIndexOf('\\') + 1)];
		}

		public string? RebuildFileList(string path, string? prefix = null)
		{
			if (isTerminal || config == null || filterElements == null)
				return null;

			bool includeRoot = config.IncludeRootDirectory;
			string targetDirectoryName = ExtractTargetName(path);

			var newFilterElements = new List<string>();

			foreach (var filter in filterElements)
			{
				try
				{
					if (Directory.EnumerateFiles(path, filter, SearchOption.AllDirectories).Any())
						newFilterElements.Add((includeRoot ? targetDirectoryName + "\\" : "") + filter);
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Error re-building file list: {ex}");
				}
			}

			string? fileListPath = null;

			if (newFilterElements.Count > 0)
			{
				try
				{
					File.WriteAllLines(fileListPath = currentExecutablePath + (prefix ?? "") + phaseName + rebuildedFileListSuffix, newFilterElements);
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Error writing re-builded file list: {ex}");
				}
			}
			else
				Console.WriteLine("Rebuild-WARNING: There's no file matching extension");

			return fileListPath;
		}

		public Task? PerformPhaseAsync(string path, string extraParameters)
		{
			if (config == null)
				return null;

			string currentTargetName = ExtractTargetName(path);

			Console.WriteLine($">> ===== ----- {phaseName} Phase (Async) ----- ===== <<");
			Program.PrintConsoleAndTitle($"Queued \"{currentTargetName}\" - {phaseName} Phase");
			Console.WriteLine();

			Task? task = null;

			try
			{
				Process sevenzip = new();
				sevenzip.StartInfo.FileName = config.SevenZipExecutable;
				sevenzip.StartInfo.WorkingDirectory = $"{(config.IncludeRootDirectory ? ExtractSuperDirectoryName(path) : path)}\\";
				sevenzip.StartInfo.Arguments = $"{config.CommonArguments} {phaseParameter} {extraParameters}";
				sevenzip.StartInfo.UseShellExecute = true;

				Console.WriteLine("Params: " + sevenzip.StartInfo.Arguments);

				sevenzip.Start();

				task = sevenzip.WaitForExitAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine("Exception while executing 7z asynchronously: " + ex.ToString());
			}

			return task;
		}

		public bool PerformPhase(string path, string indexPrefix, string extraParameters)
		{
			if (config == null)
				return false;

			string currentTargetName = ExtractTargetName(path);

			Console.WriteLine($">> ===== -----<< {phaseName} Phase >>----- ===== <<");
			Program.PrintConsoleAndTitle($"{indexPrefix} Started {indexPrefix} \"{currentTargetName}\" - {phaseName} Phase");
			Console.WriteLine();

			int errorCode = -1;

			//BinaryWriter? bw = null;
			try
			{
				//bw = new BinaryWriter(new FileStream($"{currentExecutablePath}\\{phaseName}.log", FileMode.Append, FileAccess.Write));

				Process sevenzip = new();
				sevenzip.StartInfo.FileName = config.SevenZipExecutable;
				sevenzip.StartInfo.WorkingDirectory = $"{(config.IncludeRootDirectory ? ExtractSuperDirectoryName(path) : path)}\\";
				sevenzip.StartInfo.Arguments = $"{config.CommonArguments} {phaseParameter} {extraParameters}";
				sevenzip.StartInfo.UseShellExecute = false;
				//sevenzip.StartInfo.RedirectStandardOutput = true;
				//sevenzip.StartInfo.RedirectStandardError = true;

				Console.WriteLine($"{indexPrefix} Params: {sevenzip.StartInfo.Arguments}");

				sevenzip.Start();

				//new Thread(() =>
				//{
				//	RedirectSevenZipStream(sevenzip.StandardOutput.BaseStream, bw.BaseStream);
				//}).Start();
				//new Thread(() =>
				//{
				//	RedirectSevenZipStream(sevenzip.StandardError.BaseStream, bw.BaseStream);
				//}).Start();
				//new Thread(() =>
				//{
				//	RedirectSevenZipStreamToConsole(sevenzip.StandardOutput.BaseStream);
				//}).Start();
				//new Thread(() =>
				//{
				//	RedirectSevenZipStreamToConsole(sevenzip.StandardError.BaseStream);
				//}).Start();

				sevenzip.WaitForExit();

				errorCode = sevenzip.ExitCode;

				//bw.Close();
			}
			catch (Exception ex)
			{
				//bw?.Close();
				Console.WriteLine($"{indexPrefix} Exception while executing 7z: {ex}");
			}

			bool error = errorCode != 0;

			if (error)
			{
				Console.Title = $"{indexPrefix} Error compressing \"{path}\" - {phaseName} phase";

				Console.BackgroundColor = ConsoleColor.DarkRed;
				Console.WriteLine($"Compression finished with errors/warnings. (error code {errorCode})"); // TODO: 7-zip error code dictionary are available in online.
				Console.WriteLine("Check the error message and press any key to continue.");
				Console.ReadKey();

				Console.BackgroundColor = ConsoleColor.Black;
			}

			Console.WriteLine();
			Console.WriteLine();
			Program.PrintConsoleAndTitle($"{indexPrefix} \"{currentTargetName}\" - {phaseName} Phase Finished.");

			return error;
		}

		//private static void RedirectSevenZipStream(Stream from, Stream to)
		//{
		//	int readed = from.ReadByte();

		//	while (readed != -1)
		//	{
		//		to.WriteByte((byte)readed);
		//		readed = from.ReadByte();
		//	}
		//}

		//private static void RedirectSevenZipStreamToConsole(Stream from)
		//{
		//	int readed = from.ReadByte();

		//	while (readed != -1)
		//	{
		//		Console.Write((char)readed);
		//		readed = from.ReadByte();
		//	}
		//}
	}
}