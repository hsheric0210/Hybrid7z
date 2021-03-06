using System.Collections.Concurrent;
using System.Text;

namespace Hybrid7z
{
	public class Config
	{
		private readonly IniFile config;

		public string Default7zExecutable
		{
			get; set;
		}

		public string CommonArguments
		{
			get; set;
		}

		public string[] phases
		{
			get; set;
		}

		public ConcurrentDictionary<string, string> sevenzipExecutables
		{
			get; set;
		} = new();

		public bool IncludeRootDirectory
		{
			get; set;
		}

		public Config(IniFile config)
		{
			this.config = config;

			Default7zExecutable = config.read("Executable", "7z");
			CommonArguments = config.read("BaseParameters", "7z");
			phases = config.read("Phases", "7z").Split('-');

			var includeRootStr = config.read("IncludeRootDirectory", "Misc");
			IncludeRootDirectory = !string.Equals(includeRootStr, "0") && !string.Equals(includeRootStr, "false", StringComparison.InvariantCultureIgnoreCase) && !string.Equals(includeRootStr, "no", StringComparison.InvariantCultureIgnoreCase);
		}

		public string Get7zExecutable(string phaseName)
		{
			if (sevenzipExecutables.TryGetValue(phaseName, out var exe))
				return exe;

			if (config.keyExists(phaseName, "7zExecutable"))
				exe = config.read(phaseName, "7zExecutable");
			else
				exe = Default7zExecutable;
			sevenzipExecutables[phaseName] = exe;
			return exe;
		}

		public string GetPhaseSpecificParameters(string phaseName) => config.read(phaseName, "Parameters");

		public static void SaveDefaults(string path)
		{
			var builder = new StringBuilder();

			builder.AppendLine("[7z]");

			builder.AppendLine("; 7-Zip executable name");
			builder.AppendLine("Executable=7z.exe");

			builder.AppendLine("; Common 7-Zip command-line parameters that affects on all phases");
			builder.AppendLine("BaseParameters=a -t7z -mhe -ms=1g -mqs -slp -bt -bb3 -bsp1 -sae");

			builder.AppendLine("; List of phases");
			builder.AppendLine("; Syntax: {Does the phase should run in parallel? (y or n)}{Phase name}");
			builder.AppendLine("; Example: yCopy -> Copy phase that should run in parallel; nCopy -> Copy phase that should run in sequential");
			builder.AppendLine("; Note that you MUST append y or n at the start of each phase: 'PPMd'(X) 'yPPMd'(O)");
			builder.AppendLine("Phases=yPPMd-yCopy-nLZMA2-nx86-nBrotli-nFastLZMA2");

			builder.AppendLine();
			builder.AppendLine("; You can specify 7z arguments for each phases");
			builder.AppendLine("[Parameters]");

			builder.AppendLine("; 7-Zip command-line parameters use on PPMd phase");
			builder.AppendLine("PPMd=-m0=PPMd -mx=9 -myx=9 -mmem=1024m -mo=32 -mmt=1");

			builder.AppendLine("; 7-Zip command-line parameters use on Copy phase");
			builder.AppendLine("Copy=-m0=Copy -mx=0");

			builder.AppendLine("; 7-Zip command-line parameters use on LZMA2 phase");
			builder.AppendLine("LZMA2=-m0=LZMA2 -mx=9 -myx=9 -md=512m -mfb=273 -mmt=8 -mmtf=on -mmf=bt4 -mmc=10000 -mlc=4");

			builder.AppendLine("; 7-Zip command-line parameters use on x86 phase");
			builder.AppendLine("x86=-mf=BCJ2 -m0=LZMA2 -mx=9 -myx=9 -md=512m -mfb=273 -mmt=8 -mmtf=on -mmf=bt4 -mmc=10000 -mlc=4");

			builder.AppendLine("; 7-Zip command-line parameters use on Brotli phase");
			builder.AppendLine("Brotli=-m0=Brotli -mx=11 -myx=9 -mmt=16");

			builder.AppendLine("; 7-Zip command-line parameters use on FastLZMA2 phase");
			builder.AppendLine("FastLZMA2=-m0=FLZMA2 -mx=9 -myx=9 -md=1024m -mfb=273 -mmt=16 -mmtf=on -mlc=4");

			builder.AppendLine();
			builder.AppendLine("; You can specify different 7z executable for each phases");
			builder.AppendLine("; For example, you can specify 7zG.exe instead of 7z.exe to provide GUI version of 7z to display the progress bar more simply.");
			builder.AppendLine("[7zExecutable]");

			builder.AppendLine("; LZMA2=7zG.exe");
			builder.AppendLine("; Brotli=7zG.exe");

			builder.AppendLine();
			builder.AppendLine("[Misc]");
			builder.AppendLine("; Does the archive should contain the root folder? 0 or 1");
			builder.AppendLine("IncludeRootDirectory=0");

			File.WriteAllText(path, builder.ToString());
		}
	}
}
