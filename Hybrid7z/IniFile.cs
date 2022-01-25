using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System;

// https://stackoverflow.com/questions/217902/reading-writing-an-ini-file
namespace Hybrid7z
{
	public class IniFile   // revision 11
	{
		private readonly string Path;
		private readonly string EXE = Assembly.GetExecutingAssembly().GetName().Name ?? "Program";

		[DllImport("kernel32", CharSet = CharSet.Unicode)]
		private static extern long WritePrivateProfileString(string Section, string? Key, string? Value, string FilePath);

		[DllImport("kernel32", CharSet = CharSet.Unicode)]
		private static extern int GetPrivateProfileString(string Section, string Key, string Default, StringBuilder RetVal, int Size, string FilePath);

		[DllImport("kernel32")]
		private static extern int GetLastError();

		public IniFile(string? IniPath = null) => Path = new FileInfo(IniPath ?? EXE + ".ini").FullName;

		public string Read(string Key, string? Section = null)
		{
			var RetVal = new StringBuilder(255);
			GetPrivateProfileString(Section ?? EXE, Key, "", RetVal, 255, Path);
			int lastError = GetLastError();
			if (lastError != 0)
				throw new Exception($"Failed to get value from configuration: Key={Key}, Section={Section ?? "null"}, Error=0x{lastError:X8}");
			return RetVal.ToString().Trim();
		}

		public void Write(string? Key, string? Value, string? Section = null) => WritePrivateProfileString(Section ?? EXE, Key, Value.Trim(), Path);

		public void DeleteKey(string Key, string? Section = null) => Write(Key, null, Section ?? EXE);

		public void DeleteSection(string? Section = null) => Write(null, null, Section ?? EXE);

		public bool KeyExists(string Key, string? Section = null) => Read(Key, Section).Length > 0;
	}
}