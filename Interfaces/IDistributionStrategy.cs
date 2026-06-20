using DistributedWebCrawler.Network;

namespace DistributedWebCrawler.Interfaces;

// Стратегия выбора воркера для очередной задачи (паттерн "Стратегия").
// Разные реализации = разные способы балансировки нагрузки.
public interface IDistributionStrategy
{
    // Человеко-читаемое имя стратегии (для логов и отчёта).
    string Name { get; }

    // Выбрать воркера из числа доступных.
    WorkerInfo? SelectWorker(IReadOnlyCollection<WorkerInfo> workers);
}
