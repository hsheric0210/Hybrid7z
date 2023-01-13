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
				return Path.Combine(PathUtils.GetParentDirectory(Path.GetFullPath(SpecifiedDestination)), Path.GetFileName(SpecifiedDestination));
			return SpecifiedDestination;
		}

		return Path.GetFullPath(Path.Combine(PathUtils.GetParentDirectory(PathUtils.GetDirectory(SourcePath)), Path.GetFileName(SourcePath) + ".7z"));
	}

	public string GetPasswordParameter(string paramFormat) => Password is not null ? paramFormat.FormatToken(new { Password }) : "";
}
