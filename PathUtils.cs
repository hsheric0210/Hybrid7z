namespace Hybrid7z;
public static class PathUtils
{
	public static string GetDirectory(string path)
	{
		if (new FileInfo(path).Exists)
			return Path.GetDirectoryName(path) ?? throw new AggregateException($"Directory name of {path} is null."); // containing directory of the file
		return path; // it is directory as itself
	}

	public static string GetParentDirectory(string directory)
	{
		if (!Path.IsPathFullyQualified(directory))
			directory = Path.GetFullPath(directory);
		return Path.GetDirectoryName(directory) ?? throw new AggregateException($"Parent directory name of the directory '{directory}' is null.");
	}
}
