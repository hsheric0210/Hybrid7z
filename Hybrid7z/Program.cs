using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Hybrid7z
{
	public class Program
	{
		public const string VERSION = "0.1";

		public string currentDirectory;


		public string[]? ppmdList = null;
		public string[]? lzma2List = null;
		public string[]? copyList = null;
		public string[]? x86List = null;

		private IniFile config;
		private bool includeRoot;

		public bool error;

		[Flags]
		public enum MatchPatternFlags : uint
		{
			Normal = 0x00000000,   // PMSF_NORMAL
			Multiple = 0x00000001,   // PMSF_MULTIPLE
			DontStripSpaces = 0x00010000    // PMSF_DONT_STRIP_SPACES
		}

		[DllImport("Shlwapi.dll", SetLastError = false)]
		static extern int PathMatchSpecExW([MarshalAs(UnmanagedType.LPWStr)] string file,
								   [MarshalAs(UnmanagedType.LPWStr)] string spec,
								   MatchPatternFlags flags);

		private static bool MatchPattern(string file, string spec, MatchPatternFlags flags)
		{
			if (String.IsNullOrEmpty(file))
				return false;

			if (String.IsNullOrEmpty(spec))
				return true;

			int result = PathMatchSpecExW(file, spec, flags);

			return (result == 0);
		}

		public static void Main(string[] args)
		{
			Console.WriteLine($"Hybrid7z v{VERSION}");
			Console.WriteLine("Original idea inspired from https://superuser.com/questions/311937/how-do-i-create-separate-zip-files-for-each-selected-file-directory-in-7zip");
			Console.WriteLine("Ported from Windows Batch file version");

			string cd = AppDomain.CurrentDomain.BaseDirectory;

			Console.WriteLine($"Current executable path is \"{cd}\"");

			if (!File.Exists(cd + "Hybrid7z.ini"))
			{
				Console.WriteLine("Writing default config");
				SetupDefaultConfiguration(cd);
			}

			new Program(cd, args);
		}

		public Program(string currentdir, string[] param)
		{
			currentDirectory = currentdir;

			config = new IniFile($"{currentDirectory}Hybrid7z.ini");
			includeRoot = !String.Equals(config.Read("IncludeRootDirectory"), "0");

			Console.Title = "Reading file lists...";
			PrepairFileLists();

			int totalFileCount = param.Length;

			int currentFileIndex = 1;
			foreach (string filename in param)
			{
				string titlePrefix = String.Format("[{0}/{1}] ", currentFileIndex, totalFileCount);
				if (Directory.Exists(filename))
				{
					ProcessDirectory(filename, titlePrefix);
				}
				else if (File.Exists(filename))
					Console.WriteLine($"WARNING: Currently, file are not supported (only directories are supported) - \"{filename}\"");
				else
					Console.WriteLine($"WARNING: File not exists - \"{filename}\"");
				currentFileIndex++;
			}

			if (error)
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

			var fileLists = new string[] { "PPMd.filelist.txt", "LZMA2.filelist.txt", "Copy.filelist.txt", "x86.filelist.txt" };
			foreach (string filename in fileLists)
			{
				if (File.Exists(currentDirectory + filename))
				{
					File.Delete(currentDirectory + filename);
					Console.WriteLine($"Deleted {currentDirectory}{filename}");
				}
			}
		}

		private static void SetupDefaultConfiguration(string currentDir)
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

		private void PrepairFileLists()
		{
			var fileList = new (string filename, Action<string[]> apply)[] { ("PPMd.txt", list => ppmdList = list), ("LZMA2.txt", list => lzma2List = list), ("Copy.txt", list => copyList = list), ("x86.txt", list => x86List = list) };

			foreach (var (filename, apply) in fileList)
			{
				string path = currentDirectory + filename;
				if (File.Exists(path))
				{
					Console.WriteLine($"Reading file list: {path}");
					try
					{
						Console.Title = $"Reading file list - {filename}";

						// TODO: Asynchronize
						string[] listElements = File.ReadAllLines(path);
						apply(listElements);
					}
					catch (Exception ex)
					{
						Console.WriteLine($"Error reading file: {ex.ToString()}");
					}
				}
			}
		}

		private void ProcessDirectory(string path, string titlePrefix)
		{
			string currentDirName = path.Substring(path.LastIndexOf('\\') + 1);
			string archiveName = $"{(includeRoot ? "" : "..\\")}{currentDirName}.7z";

			var rebuildedFileLists = new (string, string, string[]?)[] { ("PPMd", "PPMd.filelist.txt", ppmdList), ("LZMA2", "LZMA2.filelist.txt", lzma2List), ("Copy", "Copy.filelist.txt", copyList), ("x86", "x86.filelist.txt", x86List) };

			Console.WriteLine($"Re-building file list for {path}");

			RebuildFileList(path, currentDirName, rebuildedFileLists);

			Console.WriteLine($"Start processing {path}");
			Console.WriteLine();

			string flzma2Exclude = "";

			foreach (var (phaseName, fileName, _) in rebuildedFileLists)
			{
				if (File.Exists(currentDirectory + fileName))
				{
					flzma2Exclude += $"-xr@\"{currentDirectory}{fileName}\"";
					PerformPhase(phaseName, path, titlePrefix, $"-ir@\"{currentDirectory}{fileName}\" -- \"{archiveName}\" \"{(includeRoot ? currentDirName : "*")}\"");
				}
			}

			// TODO: Check any files to compress with FastLZMA2

			PerformPhase("FastLZMA2", path, titlePrefix, $"-r {flzma2Exclude} -- \"{archiveName}\" \"{(includeRoot ? currentDirName : "*")}\"");

			Console.WriteLine();
			Console.WriteLine($"Finished processing {path}");
		}

		private void PerformPhase(string phaseName, string path, string titlePrefix, string extraParameters)
		{
			Console.Title = $"{titlePrefix}Compressing \"{path}\" - {phaseName} phase";

			Console.WriteLine("==================================");
			Console.WriteLine($"{phaseName} phase - Phase started.");
			Console.WriteLine();

			int errcode = -1;

			try
			{
				Process sevenzip = new Process();
				sevenzip.StartInfo.FileName = config.Read("7z");
				string cd = includeRoot ? currentDirectory : path;
				sevenzip.StartInfo.WorkingDirectory = cd + "\\";
				sevenzip.StartInfo.Arguments = $"{config.Read("BaseArgs")} {config.Read($"Args_{phaseName}")} {extraParameters}";
				sevenzip.StartInfo.UseShellExecute = false;

				Console.WriteLine("Params: " + sevenzip.StartInfo.Arguments);

				sevenzip.Start();


				sevenzip.WaitForExit();

				errcode = sevenzip.ExitCode;
			}
			catch (Exception ex)
			{
				Console.WriteLine("Exception while executing 7z: " + ex.ToString());
			}

			if (errcode != 0)
			{
				Console.Title = $"{titlePrefix}Error compressing \"{path}\" - {phaseName} phase";

				Console.BackgroundColor = ConsoleColor.DarkRed;
				Console.WriteLine($"Compression finished with errors/warnings. (error code {errcode})");
				Console.WriteLine("Check the error message and press any key to continue.");
				Console.ReadKey();

				Console.BackgroundColor = ConsoleColor.Black;
				error = true;
			}

			Console.WriteLine();
			Console.WriteLine();
			Console.WriteLine($"{phaseName} phase - Phase finished.");
		}

		private void RebuildFileList(string path, string currentDirName, (string, string, string[]?)[] fileList)
		{

			foreach (var (_, filename, list) in fileList)
			{
				if (list == null) continue;

				var newFilterList = new List<string>();
				foreach (var filter in list)
				{
					try
					{
						if (Directory.EnumerateFiles(path, filter, SearchOption.AllDirectories).Any())
							newFilterList.Add((includeRoot ? currentDirName + "\\" : "") + filter);
					}
					catch (Exception ex)
					{
						Console.WriteLine($"Error rebuilding file list: {ex.ToString()}");
					}
				}

				if (newFilterList.Count > 0)
				{
					try
					{
						File.WriteAllLines(currentDirectory + filename, newFilterList);
					}
					catch (Exception ex)
					{
						Console.WriteLine($"Error writing rebuilded file list: {ex.ToString()}");
					}
				}
			}
		}
	}
}