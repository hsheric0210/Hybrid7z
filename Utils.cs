﻿using Serilog;
using System.Collections.Concurrent;

namespace Hybrid7z
{
	public static class Utils
	{
		private static readonly Dictionary<string, long> originalFilesSizeCache = new();
		private static readonly Dictionary<string, long> archiveFileSizeCache = new();
		private static readonly Dictionary<(long, int), string> sizeSuffixCache = new();

		public static (long, long) GetCompressionRatio(Target target, EnumerationOptions recursiveEnumeratorOptions, string? targetSimpleName = null)
		{
			if (!originalFilesSizeCache.TryGetValue(target.SourcePath, out var originalSize))
			{
				originalSize = new DirectoryInfo(target.SourcePath).GetFiles("*", recursiveEnumeratorOptions).Sum(f => f.Length);
				originalFilesSizeCache[target.SourcePath] = originalSize;
			}

			if (!archiveFileSizeCache.TryGetValue(target.GetDestination(), out var compressedSize))
			{
				Log.Debug("Compression ratio of {file}.", target.GetDestination());
				var file = new FileInfo(target.GetDestination());
				compressedSize = file.Exists ? file.Length : 0;
				archiveFileSizeCache[target.GetDestination()] = compressedSize;
			}

			PrintCompressionRatio($"\"{targetSimpleName ?? Path.GetFileName(target.SourcePath)}\":", originalSize, compressedSize);
			return (originalSize, compressedSize);
		}

		public static void PrintCompressionRatio(string name, long originalSize, long compressedSize)
		{
			Log.Information("DONE: {name} {sizeFrom} -> {sizeTo} (size reduction {sizeReduction}%)",
				name,
				SizeSuffix(originalSize),
				SizeSuffix(compressedSize),
				originalSize > 0 ? (compressedSize * 100 / originalSize) : 0);

			if (originalSize > compressedSize)
				Log.Information("DONE: - {name} Saved {sizeReduction}", name, SizeSuffix(originalSize - compressedSize));
			else
				Log.Warning("DONE: - {name} Wasted {sizeWasted}", name, SizeSuffix(compressedSize - originalSize));
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

		// https://stackoverflow.com/questions/14488796/does-net-provide-an-easy-way-convert-bytes-to-kb-mb-gb-etc

		private static readonly string[] SizeSuffixes = { "bytes", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };
		private static readonly string[] SizeSuffixesBinary = { "bytes", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB", "ZiB", "YiB" };

		public static string SizeSuffix(long value, int decimalPlaces = 1)
		{
			if (!sizeSuffixCache.TryGetValue((value, decimalPlaces), out var result))
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

			var mag = (int)Math.Log(value, _base);

			var adjustedSize = (decimal)value / (_base == 1024 ? (1L << (mag * 10)) : (int)Math.Pow(_base, mag));

			if (Math.Round(adjustedSize, decimalPlaces) >= 1000)
			{
				mag += 1;
				adjustedSize /= _base;
			}

			return string.Format("{0:n" + decimalPlaces + "} {1}", adjustedSize, suffixes[mag]);
		}
	}
}
