using DistributedWebCrawler.Interfaces;
using DistributedWebCrawler.Network;

namespace DistributedWebCrawler.Distribution;

// Least-Loaded ("наименее загруженный"): задача отдаётся тому воркеру,
// у которого сейчас меньше всего задач в работе.
// Лучше балансирует нагрузку, если воркеры работают с разной скоростью.
public class LeastLoadedStrategy : IDistributionStrategy
{
    public string Name => "Least-Loaded";

    public WorkerInfo? SelectWorker(IReadOnlyCollection<WorkerInfo> workers)
    {
        WorkerInfo? best = null;

        // Проходим по всем воркерам и запоминаем того, у кого меньше всего задач в работе.
        foreach (WorkerInfo worker in workers)
        {
            if (best is null || worker.InFlightCount < best.InFlightCount)
                best = worker;
        }

        return best; // null, если воркеров нет
    }
}
