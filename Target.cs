using StringTokenFormatter;

namespace Hybrid7z;

/// <summary>
/// Represents a fully-qualified archive/unarchive target
/// </summary>
/// <param name="SourceFileName">Source path (fully qualified)</param>
/// <param name="SpecifiedDestination">Destination path (fully qualified)</param>
public record Target(string SourcePath, bool IncludeRootFolder, string? SpecifiedDestination, string? Password)
{
	public string GetDestination(bool calledOnArchiver = false)
	{
		if (!string.IsNullOrWhiteSpace(SpecifiedDestination))
		{
			if (calledOnArchiver && !IncludeRootFolder && !Path.IsPathFullyQualified(SpecifiedDestination))
				return Path.Combine(Path.GetDirectoryName(SpecifiedDestination)!, "..", Path.GetFileName(SpecifiedDestination));
			return SpecifiedDestination;
		}

		var path = Path.GetDirectoryName(SourcePath) ?? "";
		if (calledOnArchiver && !IncludeRootFolder)
			path = Path.Combine(path, "..");
		return Path.Combine(path, Path.GetFileName(SourcePath) + ".7z");
	}

	public string GetPasswordParameter(string paramFormat) => Password is not null ? paramFormat.FormatToken(new { Password }) : "";
}
