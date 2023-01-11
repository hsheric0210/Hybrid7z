using Serilog;
using System.Diagnostics;
using System.Text;

namespace Hybrid7z.Phase
{
	public class SinglePhase
	{

		private readonly Config? config;
		public readonly string phaseName;
		private readonly string logFolder;
		public readonly bool isTerminal;
		public readonly bool sequential;

		public SinglePhase(Config config, string phaseName, string logFolder, bool isTerminal, bool sequential)
		{
			this.config = config;
			this.phaseName = phaseName;
			this.logFolder = logFolder;
			this.isTerminal = isTerminal;
			this.sequential = sequential;
		}

		public async Task<bool> PerformPhaseParallel(string source, string extraParameters)
		{
			if (config == null)
				return false;

			try
			{
				return await new Archiver(config, phaseName, source, logFolder).Execute(extraParameters) == 0;
			}
			catch (Exception ex)
			{
				Log.Error("Exception during archiver parallel execution", ex);
				return false;
			}
		}

		public async Task<bool> PerformPhaseSequential(string source, string indexPrefix, string extraParameters)
		{
			if (config == null)
				return false;

			var currentTargetName = Path.GetFileName(source);
			var errorCode = -1;

			Console.WriteLine($">> ===== -----<< {phaseName} Phase (Sequential) >>----- ===== <<");
			Log.Information("+++ {prefix} Phase {phase} sequential execution: {target}", indexPrefix, phaseName, source);
			Console.WriteLine();

			try
			{
				errorCode = await new Archiver(config, phaseName, source, logFolder).Execute(extraParameters);
			}
			catch (Exception ex)
			{
				Console.WriteLine();
				Log.Error(ex, "{prefix} Exception during archiver sequential execution.", indexPrefix);
			}

			var error = errorCode != 0;

			if (error)
			{
				Console.WriteLine();
				Console.Title = $"{indexPrefix} Error compressing \"{source}\" - {phaseName} phase";
				Log.Warning("Archiver process exited with non-zero exit code: {code}", errorCode);
				Log.Warning("Exit code details: {info}", Utils.Get7ZipExitCodeInformation(errorCode));
			}

			Console.WriteLine();
			Log.Debug($"{indexPrefix} \"{currentTargetName}\" - {phaseName} Phase Finished.");

			return error;
		}

	}
}
