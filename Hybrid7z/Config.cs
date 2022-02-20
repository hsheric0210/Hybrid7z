using System.Text;

namespace Hybrid7z
{
	public class Config
	{
		private readonly IniFile config;

		public string SevenZipExecutable;
		public string CommonArguments;
		public string[] phases;

		public bool IncludeRootDirectory;

		public Config(IniFile config)
		{
			this.config = config;

			SevenZipExecutable = config.read("Executable", "7z");
			CommonArguments = config.read("BaseParameters", "7z");
			phases = config.read("Phases", "7z").Split('-');

			var includeRootStr = config.read("IncludeRootDirectory", "Misc");
			IncludeRootDirectory = !string.Equals(includeRootStr, "0") && !string.Equals(includeRootStr, "false", StringComparison.InvariantCultureIgnoreCase) && !string.Equals(includeRootStr, "no", StringComparison.InvariantCultureIgnoreCase);
		}

		public string getPhaseSpecificParameters(string phaseName) => config.read($"{phaseName}", "Parameters");

		public static void saveDefaults(string path)
		{
			var builder = new StringBuilder();

			builder.AppendLine("[7z]");
			
			builder.AppendLine("; 7-Zip executable name");
			builder.AppendLine("Executable=7z.exe");
			
			builder.AppendLine("; Common 7-Zip command-line parameters that affects on all phases");
			builder.AppendLine("BaseParameters=a -t7z -mhe -ms=1g -mqs -slp -bt -bb3 -sae");

			builder.AppendLine("; List of phases");
			builder.AppendLine("; Syntax: {Does the phase should run in parallel? (y or n)}{Phase name}");
			builder.AppendLine("; Example: yCopy -> Copy phase that should run in parallel; nCopy -> Copy phase that should run in sequential");
			builder.AppendLine("; Note that you MUST append y or n at the start of each phase: 'PPMd'(X) 'yPPMd'(O)");
			builder.AppendLine("Phases=yPPMd-yCopy-nLZMA2-nx86-nBrotli-nFastLZMA2");

			builder.AppendLine();
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
			builder.AppendLine("[Misc]");
			builder.AppendLine("; Does the archive should contain the root folder? 0 or 1");
			builder.AppendLine("IncludeRootDirectory=0");

			File.WriteAllText(path, builder.ToString());
		}
	}
}
