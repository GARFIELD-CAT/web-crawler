using DistributedWebCrawler.Network;

namespace DistributedWebCrawler.Interfaces;

/// <summary>
/// Стратегия выбора воркера для очередной задачи (паттерн "Стратегия").
/// Разные реализации = разные способы балансировки нагрузки.
/// Мастер не знает деталей — он просто просит стратегию выбрать воркера,
/// поэтому алгоритм можно менять, не трогая мастер.
/// </summary>
public interface IDistributionStrategy
{
    /// <summary>Человеко-читаемое имя стратегии (для логов и отчёта).</summary>
    string Name { get; }

    /// <summary>
    /// Выбрать воркера из числа доступных.
    /// Возвращает null, если воркеров нет.
    /// </summary>
    WorkerInfo? SelectWorker(IReadOnlyCollection<WorkerInfo> workers);
}
