using Hybrid7z.Phase;
using Serilog;

namespace Hybrid7z
{
	public class Instance
	{
		public async Task Start(string cwd, IEnumerable<string> targetStrings, LaunchParameters options)
		{
			Config config = LoadConfig(options.ConfigFile ?? Path.Combine(cwd, Launch.DefaultConfigFileName));
			var logFolder = options.LogFolder ?? Path.Combine(cwd, Launch.DefaultLogFolder);
			var filterFolder = options.PhaseFilterFolder ?? Path.Combine(cwd, ".");
			var exclusionFilePath = Path.Combine(filterFolder, "Exclude.txt");

			var targetStringList = new List<string>(targetStrings);
			if (options.BatchFile is not null)
				targetStringList.AddRange(await File.ReadAllLinesAsync(options.BatchFile));
			ICollection<Target> targetList = await ParseTargets(targetStringList, config.IncludeRootDirectory);
			var phaseList = new PhaseList(config, logFolder, filterFolder);
			await phaseList.AddTargets(targetList);

			if (new FileInfo(exclusionFilePath).Exists)
				phaseList.AddGlobalExclusionListFile(exclusionFilePath);

			Log.Debug("+++ Compression");
			if (targetList.Count == 0)
				Log.Error("No target supplied.");
			else if (await phaseList.Process())
				Log.Warning("At least one error/warning occurred during the progress.");
			else
			{
				Log.Debug("DONE: All files are successfully proceed without any error(s).");
				if (config.DeleteFilterCache)
					await phaseList.DeleteFilterCache();
				if (config.DeleteArchivedPath)
					await DeleteArchivedPaths(targetList);
			}
		}

		private async Task DeleteArchivedPaths(IEnumerable<Target> targetList) => await targetList.ForEachParallel(targetList => Shell32.MoveToRecycleBin(targetList.SourcePath));

		private static Config LoadConfig(string configFile, bool again = false)
		{
			Log.Information("+++ Config loading");

			try
			{
				return new Config(configFile);
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Exception during config loading. Writing and loading default config file.");
				if (again) // If it is already default config
					throw new AggregateException("Default configuration exception");
				else
					Log.Warning(ex, "Config loading error. Using default config.");
				File.Move(configFile, configFile + ".bak");
				Config.SaveDefaults(configFile);
				return LoadConfig(configFile, true);
			}
		}

		private async Task<ICollection<Target>> ParseTargets(IEnumerable<string> targetStrings, bool includeRoot)
		{
			Log.Debug("+++ Querying targets");
			var tasks = new List<Task<Target?>>();
			foreach (var param in targetStrings)
			{
				var path = param.Split('|', 3); // <source>|<destination>|<password>
				tasks.Add(Task.Run(() =>
				{
					if (Directory.Exists(path[0]))
					{
						Log.Information("Found source directory: {path}", path[0]);
						return new Target(path[0], includeRoot, path.Length > 1 ? path[1] : null, path.Length > 2 ? path[2] : null);
					}
					else if (File.Exists(path[0]))
					{
						Log.Warning("File are not supported (only directories are supported): {path}", path);
					}
					else
					{
						Log.Warning("Filesystem entry not exists: {path}", path);
					}

					return null;
				}));
			}
			Log.Debug("--- Querying targets");
			return (from list in await Task.WhenAll(tasks) where list is not null select list).ToList();
		}
	}
}
