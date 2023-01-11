using StringTokenFormatter;

namespace Hybrid7z;

/// <summary>
/// Represents a fully-qualified archive/unarchive target
/// </summary>
/// <param name="SourceFileName">Source path (fully qualified)</param>
/// <param name="SpecifiedDestination">Destination path (fully qualified)</param>
public record Target(string SourcePath, bool IncludeRootFolder, string? SpecifiedDestination, string? Password)
{
	public string GetDestination()
	{
		if (!string.IsNullOrWhiteSpace(SpecifiedDestination))
		{
			if (!IncludeRootFolder && !Path.IsPathFullyQualified(SpecifiedDestination))
				return Path.Combine(GetDirectoryName(SpecifiedDestination)!, "..", Path.GetFileName(SpecifiedDestination));
			return SpecifiedDestination;
		}

		var path = GetDirectoryName(SourcePath) ?? "";
		if (!IncludeRootFolder)
			path = Path.Combine(path, "..");
		return Path.Combine(path, Path.GetFileName(SourcePath) + ".7z");
	}

	private static string GetDirectoryName(string path)
	{
		if (new FileInfo(path).Exists)
			return Path.GetDirectoryName(path)!; // containing directory of the file
		return path; // it is directory as itself
	}

	public string GetPasswordParameter(string paramFormat) => Password is not null ? paramFormat.FormatToken(new { Password }) : "";
}
