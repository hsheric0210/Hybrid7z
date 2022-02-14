using System.Collections.Concurrent;

namespace Hybrid7z
{
	public static class Utils
	{
		private static readonly ConcurrentDictionary<string, string> targetNameCache = new();
		private static readonly ConcurrentDictionary<string, string> superNameCache = new();
		private static readonly Dictionary<string, long> originalFilesSizeCache = new();
		private static readonly Dictionary<string, long> archiveFileSizeCache = new();


		public static void printCompressionRatio(string target, EnumerationOptions recursiveEnumeratorOptions, string? targetSimpleName = null)
		{
			trimTrailingPathSeparators(ref target);

			if (!originalFilesSizeCache.TryGetValue(target, out var originalSize))
			{
				originalSize = new DirectoryInfo(target).GetFiles("*", recursiveEnumeratorOptions).Sum(f => f.Length);
				originalFilesSizeCache[target] = originalSize;
			}

			if (!archiveFileSizeCache.TryGetValue(target, out var compressedSize))
			{
				compressedSize = new FileInfo($"{target}.7z").Length;
				archiveFileSizeCache[target] = compressedSize;
			}

			Console.WriteLine($"Compression Ratio: \"{targetSimpleName ?? Utils.extractTargetName(target)}\" is {(originalSize > 0 ? (compressedSize * 100 / originalSize) : 0)}% compressed ({originalSize} -> {compressedSize} bytes)");
		}

		public static string extractTargetName(string path)
		{
			if (targetNameCache.TryGetValue(path, out string? targetName))
				return targetName;

			trimTrailingPathSeparators(ref path);

			targetName = path[(path.LastIndexOf('\\') + 1)..];
			targetNameCache.TryAdd(path, targetName);
			return targetName;
		}

		public static string extractSuperDirectoryName(string path)
		{
			if (superNameCache.TryGetValue(path, out string? superName))
				return superName;

			trimTrailingPathSeparators(ref path);

			superName = path[..(path.LastIndexOf('\\') + 1)];
			superNameCache.TryAdd(path, superName);
			return superName;
		}

		public static void printConsoleAndTitle(string message)
		{
			Console.WriteLine(message);
			Console.Title = message;
		}

		public static void trimTrailingPathSeparators(ref string path)
		{
			while (path.EndsWith('\\'))
				path = path[0..^1]; // Drop last char
		}

		public static void trimLeadingPathSeparators(ref string path)
		{
			while (path.StartsWith('\\'))
				path = path[1..]; // Drop first char
		}

		public static string get7ZipExitCodeInformation(int exitCode) => exitCode switch
		{
			1 => "Non-fatal warning(s)",
			2 => "Fatal error",
			7 => "Command-line error",
			8 => "Not enough memory for operation",
			255 => "User stopped the process",
			_ => "",
		};

		public static void pause()
		{
			string? line;
			do
				line = Console.ReadLine();
			while ((line?.Length ?? 0) <= 0);
		}

		public static void printError(string prefix, string details)
		{
			ConsoleColor prevColor = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Red;
			printConsoleAndTitle($"[{prefix}] {details}");
			Console.WriteLine();
			Console.WriteLine($"[{prefix}] Check the error details and press any key and enter to continue process...");
			pause();
			Console.ForegroundColor = prevColor;
		}
	}
}
