using Hybrid7z.Phase.Filter;
using Serilog;
using static Hybrid7z.Phase.SinglePhase;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Hybrid7z.Phase;
internal class PhaseList
{
	public static readonly EnumerationOptions RecursiveEnumeratorOptions = new()
	{
		AttributesToSkip = 0,
		RecurseSubdirectories = true
	};

	private readonly Config config;
	private readonly PhaseFilterList filterList;

	private readonly List<Target> targetList = new();
	private readonly IList<SinglePhase> phaseList = new List<SinglePhase>();
	private readonly IList<string> AdditionalParameters = new List<string>();

	public PhaseList(Config config, string logFolder, string filterFolder)
	{
		this.config = config;

		IReadOnlyList<string> phaseNameList = config.PhaseList;
		var filters = new List<PhaseFilter>();
		for (int i = 0, j = phaseNameList.Count; i < j; i++)
		{
			var phaseName = phaseNameList[i];
			var isTerminal = i == j - 1;
			try
			{
				var parallel = config.IsPhaseParallel(phaseNameList[i]);
				phaseList.Add(new SinglePhase(config, phaseName, logFolder, isTerminal, parallel));
				if (!isTerminal)
					filters.Add(new PhaseFilter(config, phaseName, filterFolder));

				Log.Information("Phase construted: name={name}, terminal={terminal}, parallel={parallel}", phaseName, isTerminal, parallel);
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Exception during construction of phase {name}.", phaseName);
			}
		}
		filterList = new PhaseFilterList(filters);
	}

	public async Task AddTargets(IEnumerable<Target> targets)
	{
		targetList.AddRange(targets);
		filterList.AddSources(targets.Select(t => t.SourcePath).ToList());
		await filterList.Parse(); // TODO: Move to another func
		await filterList.Rebuild();
	}

	public void AddGlobalExclusionListFile(string listFileName) => AdditionalParameters.Add($"-xr@\"{listFileName}\"");

	public async Task<bool> Process()
	{
		var error = false;
		var sequentialPhases = new List<SinglePhase>();
		var extraParameters = string.Join(' ', AdditionalParameters);
		foreach (SinglePhase phase in phaseList)
		{
			if (phase.sequential)
				error = await RunParallelPhase(phase, targetList, extraParameters) || error;
			else
				sequentialPhases.Add(phase);
		}

		var totalTargetCount = targetList.Count;
		var currentFileIndex = 1;
		foreach (Target target in targetList)
		{
			var titlePrefix = $"[{currentFileIndex}/{totalTargetCount}]";
			error = await RunSequentialPhases(sequentialPhases, target, titlePrefix, extraParameters) || error;
			currentFileIndex++;
		}

		long original = 0;
		long compressed = 0;
		foreach (Target target in targetList)
		{
			(long original, long compressed) currentRatio = Utils.GetCompressionRatio(target, RecursiveEnumeratorOptions);
			original += currentRatio.original;
			compressed += currentRatio.compressed;
		}

		Utils.PrintCompressionRatio("(Overall)", original, compressed);

		return error;
	}

	private async Task<bool> RunSequentialPhases(IEnumerable<SinglePhase> phases, Target target, string indexPrefix, string extraParameters)
	{
		var includeRoot = config.IncludeRootDirectory;
		var sourceName = Path.GetFileName(target.SourcePath);
		var dest = target.GetDestination(true);
		var errorOccurred = false;

		Log.Debug("+++ Phase sequential execution: {target}", target);

		foreach (SinglePhase phase in phases)
		{
			if (phase.isTerminal)
			{
				if (filterList.TerminalPhaseInclusion.Contains(target.SourcePath))
				{
					var list = "";
					if (filterList.Rebuilded.TryGetValue(sourceName, out IDictionary<string, string>? map))
						list = string.Join(' ', map.Values.Select(from => $"-xr@\"{from}\""));

					errorOccurred = await phase.PerformPhaseSequential(target.SourcePath, indexPrefix, $"{extraParameters} {target.GetPasswordParameter(config.PasswordParameterFormat)} -r {list} -- \"{dest}\" \"{(includeRoot ? sourceName : "*")}\"") || errorOccurred;
				}
				else
				{
					Log.Warning("Terminal phase ({phase}) skip: {target}", phase.phaseName, target);
				}
			}
			else if (filterList.Rebuilded.TryGetValue(sourceName, out IDictionary<string, string>? map)
				&& map.TryGetValue(phase.phaseName, out var fileListPath)
				&& fileListPath != null)
			{
				errorOccurred = await phase.PerformPhaseSequential(
					target.SourcePath,
					indexPrefix,
					$"{extraParameters} {target.GetPasswordParameter(config.PasswordParameterFormat)} -ir@\"{fileListPath}\" -- \"{dest}\"") || errorOccurred;
			}
		}

		Console.WriteLine();
		Utils.GetCompressionRatio(target, RecursiveEnumeratorOptions, sourceName);
		Log.Information("+++ Phase sequential execution: {target}", target);

		return errorOccurred;
	}

	private async Task<bool> RunParallelPhase(SinglePhase phase, IEnumerable<Target> pairs, string extraParameters)
	{
		var phaseName = phase.phaseName;
		var error = 0;
		Log.Debug("+++ Phase parallel execution: {phase}", phaseName);
		try
		{
			await Parallel.ForEachAsync(pairs, async (target, _) =>
			{
				var archivePath = target.GetDestination(true);
				if (filterList.Rebuilded.TryGetValue(target.SourcePath, out var fileListMap)
					&& fileListMap.TryGetValue(phaseName, out var filterPath)
					&& filterPath is not null
					&& !await phase.PerformPhaseParallel(target.SourcePath,
					$"{extraParameters} {target.GetPasswordParameter(config.PasswordParameterFormat)} -ir@\"{filterPath}\" -- \"{archivePath}\""))
				{
					Interlocked.CompareExchange(ref error, 1, 0);
				}
			});
		}
		catch (AggregateException ex)
		{
			Log.Error(ex, "AggregateException during parallel execution of phase: {phase}", phaseName);
			error = 1;
		}

		if (error != 0)
			Log.Error("Error during parallel execution of phase: {phase}", phaseName);
		Log.Information("--- Phase parallel execution: {phase}", phaseName);
		return error != 0;
	}

	public async Task DeleteFilterCache() => await filterList.DeleteRebuilded();
}
