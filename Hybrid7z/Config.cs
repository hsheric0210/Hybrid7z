namespace Hybrid7z
{
	public class Config
	{
		private readonly IniFile config;

		public string SevenZipExecutable;
		public string CommonArguments;
		public bool IncludeRootDirectory;

		public Config(IniFile config)
		{
			this.config = config;

			SevenZipExecutable = config.read("Executable", "7z");
			CommonArguments = config.read("BaseParameters", "7z");

			var includeRootStr = config.read("IncludeRootDirectory", "Misc");
			IncludeRootDirectory = !string.Equals(includeRootStr, "0") && !string.Equals(includeRootStr, "false", StringComparison.InvariantCultureIgnoreCase) && !string.Equals(includeRootStr, "no", StringComparison.InvariantCultureIgnoreCase);
		}

		public string getPhaseSpecificParameters(string phaseName) => config.read($"{phaseName}", "Parameters");

		public static void saveDefaults(string path)
		{
			var ini = new IniFile(path);
			ini.write("Executable", "7z.exe", "7z");
			ini.write("BaseParameters", "a -t7z -mhe -ms=1g -mqs -slp -bt -bb3 -sae", "7z");

			ini.write("PPMd", "-m0=PPMd -mx=9 -myx=9 -mmem=1024m -mo=32 -mmt=1", "Parameters");
			ini.write("LZMA2", "-m0=LZMA2 -mx=9 -myx=9 -md=256m -mfb=273 -mmt=8 -mmtf -mmf=bt4 -mmc=10000 -mlc=4", "Parameters");
			ini.write("Copy", "-m0=Copy -mx=0", "Parameters");
			ini.write("x86", "-mf=BCJ2 -m0=LZMA2 -mx=9 -myx=9 -md=1024m -mfb=273 -mmt=8 -mmtf -mmf=bt4 -mmc=10000 -mlc=4", "Parameters");
			ini.write("FastLZMA2", "-m0=FLZMA2 -mx=9 -myx=9 -md=1024m -mfb=273 -mmt=32 -mmtf -mlc=4", "Parameters");

			ini.write("IncludeRootDirectory", "0", "Misc");
		}
	}
}
