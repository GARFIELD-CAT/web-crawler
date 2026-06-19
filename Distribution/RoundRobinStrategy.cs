using DistributedWebCrawler.Interfaces;
using DistributedWebCrawler.Network;

namespace DistributedWebCrawler.Distribution;

/// <summary>
/// Round-Robin ("по кругу"): задачи раздаются воркерам по очереди —
/// первому, второму, третьему, снова первому и так далее.
/// Простая и честная стратегия, когда все воркеры примерно одинаковы.
/// </summary>
public class RoundRobinStrategy : IDistributionStrategy
{
    // Атомарный счётчик: какой воркер был выбран последним.
    // Начинаем с -1, чтобы первый Increment дал 0 (первый воркер).
    private int _counter = -1;

    public string Name => "Round-Robin";

    public WorkerInfo? SelectWorker(IReadOnlyCollection<WorkerInfo> workers)
    {
        if (workers.Count == 0)
            return null;

        // Превращаем коллекцию в список, чтобы обращаться по индексу.
        IList<WorkerInfo> list = workers as IList<WorkerInfo> ?? workers.ToList();

        // Interlocked.Increment безопасно увеличивает счётчик из любого потока.
        // Берём остаток от деления на количество воркеров, чтобы "ходить по кругу".
        int index = (int)((uint)Interlocked.Increment(ref _counter) % (uint)list.Count);
        return list[index];
    }
}
