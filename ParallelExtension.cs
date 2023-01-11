namespace Hybrid7z;
public static class ParallelExtension
{
	public static async Task ForEachParallel<T>(this IEnumerable<T> enumerable, Action<T> syncConsumer)
	{
		var taskList = new List<Task>();
		foreach (T item in enumerable)
		{
			taskList.Add(Task.Run(() => syncConsumer(item)));
		}
		await Task.WhenAll(taskList);
	}

	public static async Task ForEachParallel<T>(this IEnumerable<T> enumerable, Func<T, Task> asyncConsumer)
	{
		var taskList = new List<Task>();
		foreach (T item in enumerable)
		{
			taskList.Add(Task.Run(async () => await asyncConsumer(item)));
		}
		await Task.WhenAll(taskList);
	}
}
