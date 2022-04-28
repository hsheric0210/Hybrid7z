using System.Collections.Concurrent;

namespace Hybrid7z
{
	public static class Utils
	{
		private static readonly ConcurrentDictionary<string, string> targetNameCache = new();
		private static readonly ConcurrentDictionary<string, string> superNameCache = new();
		private static readonly Dictionary<string, long> originalFilesSizeCache = new();
		private static readonly Dictionary<string, long> archiveFileSizeCache = new();
		private static readonly Dictionary<(long, int), string> sizeSuffixCache = new();

		public static (long, long) GetCompressionRatio(string target, EnumerationOptions recursiveEnumeratorOptions, string? targetSimpleName = null)
		{
			TrimTrailingPathSeparators(ref target);

			if (!originalFilesSizeCache.TryGetValue(target, out var originalSize))
			{
				originalSize = new DirectoryInfo(target).GetFiles("*", recursiveEnumeratorOptions).Sum(f => f.Length);
				originalFilesSizeCache[target] = originalSize;
			}

			if (!archiveFileSizeCache.TryGetValue(target, out var compressedSize))
			{
				var file = new FileInfo($"{target}.7z");
				compressedSize = file.Exists ? file.Length : 0;
				archiveFileSizeCache[target] = compressedSize;
			}

			PrintCompressionRatio($"\"{targetSimpleName ?? Utils.ExtractTargetName(target)}\":", originalSize, compressedSize);

			return (originalSize, compressedSize);
		}

		public static void PrintCompressionRatio(string name, long originalSize, long compressedSize)
		{
			Console.WriteLine($"{name} {SizeSuffix(originalSize)} -> {SizeSuffix(compressedSize)} ({(originalSize > 0 ? (compressedSize * 100 / originalSize) : 0)}% compressed)");

			ConsoleColor prevColor = Console.ForegroundColor;
			if (originalSize > compressedSize)
			{
				Console.ForegroundColor = ConsoleColor.Blue;
				Console.WriteLine($"{name} Saved {SizeSuffix(originalSize - compressedSize)}");
			}
			else
			{
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine($"{name} Wasted {SizeSuffix(compressedSize - originalSize)}");
			}

			Console.ForegroundColor = prevColor;

		}

		public static string ExtractTargetName(string path)
		{
			if (targetNameCache.TryGetValue(path, out string? targetName))
				return targetName;

			TrimTrailingPathSeparators(ref path);

			targetName = path[(path.LastIndexOf('\\') + 1)..];
			targetNameCache.TryAdd(path, targetName);
			return targetName;
		}

		public static string ExtractSuperDirectoryName(string path)
		{
			if (superNameCache.TryGetValue(path, out string? superName))
				return superName;

			TrimTrailingPathSeparators(ref path);

			superName = path[..(path.LastIndexOf('\\') + 1)];
			superNameCache.TryAdd(path, superName);
			return superName;
		}

		public static void PrintConsoleAndTitle(string message, string? _namespace = null)
		{
			if (_namespace != null)
				message = $"[{_namespace}] {message}";
			Console.WriteLine(message);
			Console.Title = message;
		}

		public static void PrintConsole(string message, string? _namespace = null)
		{
			if (_namespace != null)
				message = $"[{_namespace}] {message}";
			Console.WriteLine(message);
		}

		public static void TrimTrailingPathSeparators(ref string path)
		{
			while (path.EndsWith('\\'))
				path = path[0..^1]; // Drop last char
		}

		public static void TrimLeadingPathSeparators(ref string path)
		{
			while (path.StartsWith('\\'))
				path = path[1..]; // Drop first char
		}

		public static string Get7ZipExitCodeInformation(int exitCode) => exitCode switch
		{
			1 => "Non-fatal warning(s)",
			2 => "Fatal error",
			7 => "Command-line error",
			8 => "Not enough memory for operation",
			255 => "User stopped the process",
			_ => "",
		};

		public static void Pause()
		{
			string? line;
			do
				line = Console.ReadLine();
			while ((line?.Length ?? 0) <= 0);
		}

		public static void PrintError(string prefix, string details)
		{
			ConsoleColor prevColor = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Red;
			PrintConsole($"[{prefix}] {details}");
			Console.WriteLine();
			Console.WriteLine($"[{prefix}] Check the error details and press any key and enter to continue process...");
			Pause();
			Console.ForegroundColor = prevColor;
		}

		// https://stackoverflow.com/questions/14488796/does-net-provide-an-easy-way-convert-bytes-to-kb-mb-gb-etc

		private static readonly string[] SizeSuffixes = { "bytes", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };
		private static readonly string[] SizeSuffixesBinary = { "bytes", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB", "ZiB", "YiB" };

		public static string SizeSuffix(long value, int decimalPlaces = 1)
		{
			if (!sizeSuffixCache.TryGetValue((value, decimalPlaces), out string? result))
			{
				result = $"({SizeSuffixInternal(value, decimalPlaces, SizeSuffixes, 1000)} / {SizeSuffixInternal(value, decimalPlaces, SizeSuffixesBinary, 1024)})";
				sizeSuffixCache[(value, decimalPlaces)] = result;
			}

			return result;
		}

		private static string SizeSuffixInternal(long value, int decimalPlaces, string[] suffixes, int _base)
		{
			if (decimalPlaces < 0)
				throw new ArgumentOutOfRangeException(nameof(decimalPlaces));

			if (value < 0)
				return "-" + SizeSuffixInternal(-value, decimalPlaces, suffixes, _base);

			if (value == 0)
				return string.Format("{0:n" + decimalPlaces + "} bytes", 0);

			int mag = (int)Math.Log(value, _base);

			decimal adjustedSize = (decimal)value / (_base == 1024 ? (1L << (mag * 10)) : (int)Math.Pow(_base, mag));

			if (Math.Round(adjustedSize, decimalPlaces) >= 1000)
			{
				mag += 1;
				adjustedSize /= _base;
			}

			return string.Format("{0:n" + decimalPlaces + "} {1}", adjustedSize, suffixes[mag]);
		}
	}
}
