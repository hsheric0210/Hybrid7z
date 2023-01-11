using Serilog;

namespace Hybrid7z.Phase.Filter;
public class PhaseFilter
{
	public const string FILELIST_SUFFIX = ".txt";
	public const string REBUILDED_FILELIST_SUFFIX = ".lst";

	private readonly Config config;
	private readonly string filterFolder;

	private string[]? FilterElements;

	public string PhaseName { get; }

	public PhaseFilter(Config config, string phaseName, string filterFolder)
	{
		PhaseName = phaseName;
		this.config = config;
		this.filterFolder = filterFolder;
	}

	public async Task ParseFileList()
	{
		var filterPath = Path.Combine(filterFolder, PhaseName + FILELIST_SUFFIX);
		if (File.Exists(filterPath))
		{
			Log.Information("Parsing file list: {path}", filterPath);

			static string DropLeadingPathSeparators(string trimmed)
			{
				Utils.TrimLeadingPathSeparators(ref trimmed);
				return trimmed;
			}

			try
			{
				var lines = await File.ReadAllLinesAsync(filterPath);

				FilterElements = (from line in lines
								  let commentRemoved = line.Contains("//") ? line[..line.IndexOf("//")] : line
								  where !string.IsNullOrWhiteSpace(commentRemoved)
								  select DropLeadingPathSeparators(commentRemoved.Trim())).ToArray();
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Error reading phase filter file: {path}", filterPath);
			}
		}
		else
		{
			Log.Warning("Phase filter file not found: {path}", filterPath);
		}
	}

	public string? RebuildFilter(string path, string fileNamePrefix, EnumerationOptions recursiveEnumeratorOptions, WrappedTargetFiles targetFiles)
	{
		if (/*isTerminal ||*/ config == null || FilterElements == null)
			return null;

		var includeRoot = config.IncludeRootDirectory;
		var targetDirectoryName = Path.GetFileName(path);

		var newFilterElements = new List<string>();
		//HashSet<string>? availableFilesForThisTarget_ = availableFilesForThisTarget; // FIXME: 보아하니 Hybrid7z.cs#112 의 fixme가 불가능한 게 여기서 그대로 Closure 안으로 ref 파라미터를 넘길 수 없어서 그런 것 같군. 그러면 record 하나 만든 후 거기에 집어넣어서 넘기면 되는거 아님?
		Parallel.ForEach(FilterElements, filter =>
		{
			try
			{
				if (!filter.Contains('\\') || Directory.Exists(Path.Combine(path, Utils.ExtractSuperDirectoryName(filter))))
				{
					IEnumerable<string> files = Directory.EnumerateFiles(path, filter, recursiveEnumeratorOptions);
					if (files.Any())
					{
						Log.Information("Found files for filter: filter={filter}, path={path}", filter, path);
						newFilterElements.Add((includeRoot ? targetDirectoryName + "\\" : "") + filter);

						lock (targetFiles)
						{
							foreach (var filepath in files)
								targetFiles.TargetFiles.Remove(filepath.ToUpperInvariant()); // Very inefficient solution; But, at least, hey, it iss working!
						}
					}
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Exception during phase filter rebuilding: {path}", path);
			}
		});

		string? filterPath = null;

		if (newFilterElements.Count > 0)
		{
			filterPath = Path.Combine(filterFolder, fileNamePrefix + PhaseName + REBUILDED_FILELIST_SUFFIX);
			try
			{
				File.WriteAllLines(filterPath, newFilterElements);
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Exception during phase filter rewriting: {path}", filterPath);
			}
		}

		return filterPath;
	}
}
