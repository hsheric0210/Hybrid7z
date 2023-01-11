using Serilog;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hybrid7z.Phase.Filter;
public class PhaseFilterList
{
	private readonly IEnumerable<PhaseFilter> filters;
	private readonly ConcurrentDictionary<string, IDictionary<string, string>> rebuilded = new();
	private readonly HashSet<string> terminalInclusion = new();
	private readonly List<string> sources = new();

	public IReadOnlyDictionary<string, IDictionary<string, string>> Rebuilded => rebuilded;
	public IReadOnlySet<string> TerminalPhaseInclusion => terminalInclusion;

	public PhaseFilterList(IEnumerable<PhaseFilter> filters) => this.filters = filters;

	public void AddSource(string source) => sources.Add(source);

	public void AddSources(IEnumerable<string> sources) => this.sources.AddRange(sources);

	public async Task Parse() => await filters.ForEachParallel(async filter => await filter.ParseFileList());


	/// <summary>
	/// Rebuilds phase filter for specified sources
	/// </summary>
	/// <param name="sources"></param>
	/// <returns></returns>
	public async Task Rebuild()
	{
		var sw = new Stopwatch();
		Log.Debug("+++ Phase filter rebuilding");
		try
		{
			sw.Start();
			// KEY: target
			// VALUE: Set of available files
			ConcurrentDictionary<string, HashSet<string>> availableFiles = new();
			var tasks = new List<Task>();
			foreach (PhaseFilter filter in filters)
			{
				var phaseName = filter.PhaseName;
				tasks.Add(Parallel.ForEachAsync(sources, (src, _) =>
				{
					// FIXME: 얘 원래 여기가 아니라 Phase foreach 바깥에 있어야지 원래 의도한대로 'RebuildFileList에서 필터에 맞는 파일들 쳐낸 후, files에서 해당 파일 제'함으로써 '이미 어떤 페이즈를 통해 처리된 파일이 다른 페이즈를 통해 다시 처리되는 것을 방지'하는 역할을 수행할 수 있는 것 아닌가?
					var files = new WrappedTargetFiles(Directory
						.EnumerateFiles(src, "*", PhaseList.RecursiveEnumeratorOptions)
						.Select(path => path.ToUpperInvariant())
						.ToHashSet());

					var targetName = Path.GetFileName(src);
					Log.Debug("Phase {phase} filter rebuild: {path}", phaseName, targetName);

					// This will remove all paths(files) matching the specified filter from availableFilesForThisTarget.
					var filterPath = filter.RebuildFilter(src, $"{targetName}.", PhaseList.RecursiveEnumeratorOptions, files);

					// Fail-safe
					if (!rebuilded.ContainsKey(targetName))
						rebuilded[targetName] = new Dictionary<string, string>();

					IDictionary<string, string> map = rebuilded[targetName];
					if (filterPath != null)
						map.Add(phaseName, filterPath);
					availableFiles[src] = files.TargetFiles;
					return default;
				}));
			}
			await Task.WhenAll(tasks);
			foreach (var target in sources) // TODO: Improve this ugly solution
			{
				if (availableFiles.TryGetValue(target, out HashSet<string>? avail) && avail.Count > 0)
					terminalInclusion.Add(target);
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Exception during phase filter rebuilding.");
		}
		sw.Stop();
		Log.Information("--- Phase filter rebuilding: {took}ms", sw.ElapsedMilliseconds);
	}

	public async Task DeleteRebuilded()
	{
		await rebuilded.Values.SelectMany(reb => reb.Values).ForEachParallel(file =>
		{
			if (file != null && File.Exists(file))
			{
				Log.Debug("Rebuilded phase filter deletion: {path}", file);
				File.Delete(file); // this blocks the execution
			}
		});
	}
}
