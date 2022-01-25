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

			SevenZipExecutable = config.Read("7z");
			CommonArguments = config.Read("BaseArgs");
			IncludeRootDirectory = !string.Equals(config.Read("IncludeRootDirectory"), "0");
		}

		public string GetPhaseSpecificParameters(string phaseName) => config.Read($"Args_{phaseName}");
	}
}
