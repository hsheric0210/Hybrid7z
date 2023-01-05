using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace Hybrid7z
{
	public class Config
	{
		public const string ConfigFileName = "Hybrid7z.toml";

		private readonly TomlTable toml;
		private readonly ConcurrentDictionary<string, string> archiverExecutableCache = new();

		public string GlobalArchiverExecutable
		{
			get; set;
		}

		public string GlobalArchiverParameters
		{
			get; set;
		}

		public IReadOnlyList<string> PhaseList
		{
			get; set;
		}

		public bool IncludeRootDirectory
		{
			get; set;
		}

		public Config(string path)
		{
			toml = Toml.ToModel(File.ReadAllText(path, Encoding.UTF8), path);
			GlobalArchiverExecutable = (string)((TomlTable)toml["archiver"])["executable"];
			GlobalArchiverParameters = (string)((TomlTable)toml["archiver"])["parameters"];
			PhaseList = ((TomlArray)((TomlTable)toml["phase"])["phase_list"]).Select(o => o?.ToString() ?? "").ToList();
			IncludeRootDirectory = (bool)((TomlTable)toml["misc"])["include_root_folder"];
		}

		public string Get7zExecutable(string phase)
		{
			if (archiverExecutableCache.TryGetValue(phase, out var exe))
				return exe;

			var table = (TomlTable)((TomlTable)toml["phase"])["archiver_override"];

			if (table.ContainsKey(phase))
				exe = (string)table[phase];
			else
				exe = GlobalArchiverExecutable;
			archiverExecutableCache[phase] = exe;
			return exe;
		}

		public string GetPhaseSpecificParameters(string phase) => (string)((TomlTable)((TomlTable)toml["phase"])["parameters"])[phase];

		public bool IsPhaseParallel(string phase) => (bool)((TomlTable)((TomlTable)toml["phase"])["parallel"])[phase];

		public static void SaveDefaults(string path)
		{
			using Stream? rs = Assembly.GetExecutingAssembly().GetManifestResourceStream(nameof(Hybrid7z) + ".DefaultConfig.toml");
			if (rs is null)
				throw new NotSupportedException("Can't find DefaultConfig.toml resource from the assembly.");
			using FileStream fs = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
			rs.CopyTo(fs);
		}
	}
}
