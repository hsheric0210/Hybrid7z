using System.Collections.Concurrent;

namespace Hybrid7z
{
	public static class Utils
	{
		private static readonly ConcurrentDictionary<string, string> TargetNameCache = new();
		private static readonly ConcurrentDictionary<string, string> SuperNameCache = new();

		public static string ExtractTargetName(string path)
		{
			if (TargetNameCache.TryGetValue(path, out string? targetName))
				return targetName;

			TrimTrailingPathSeparators(ref path);

			targetName = path[(path.LastIndexOf('\\') + 1)..];
			TargetNameCache.TryAdd(path, targetName);
			return targetName;
		}

		public static string ExtractSuperDirectoryName(string path)
		{
			if (SuperNameCache.TryGetValue(path, out string? superName))
				return superName;

			TrimTrailingPathSeparators(ref path);

			superName = path[..(path.LastIndexOf('\\') + 1)];
			SuperNameCache.TryAdd(path, superName);
			return superName;
		}

		public static void PrintConsoleAndTitle(string message)
		{
			Console.WriteLine(message);
			Console.Title = message;
		}

		public static void TrimTrailingPathSeparators(ref string path)
		{
			while (path.EndsWith("\\"))
				path = path[(path.LastIndexOf('\\') + 1)..];
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
			ConsoleKeyInfo ci;
			do
				ci = Console.ReadKey();
			while (ci.Modifiers != 0);
		}

		public static void PrintError(string prefix, string details)
		{
			ConsoleColor prevColor = Console.BackgroundColor;
			Console.BackgroundColor = ConsoleColor.DarkRed;
			PrintConsoleAndTitle($"[{prefix}] {details}");
			Console.WriteLine();
			Console.WriteLine($"[{prefix}] Check the error details and press any key to continue process...");
			Pause();
			Console.BackgroundColor = prevColor;
		}
	}
}
