using System.Diagnostics;
using DistributedWebCrawler.Interfaces;

namespace DistributedWebCrawler.Monitoring;

// Монитор системы: раз в секунду выводит текущее состояние —
// сколько воркеров живо, сколько задач в очереди, сколько страниц обработано и с какой скоростью.
public class SystemMonitor
{
    private readonly Statistics _stats;
    private readonly Func<int> _aliveWorkers;
    private readonly Func<int> _queueSize;
    private readonly ILogger _logger;

    public SystemMonitor(Statistics stats, Func<int> aliveWorkers, Func<int> queueSize, ILogger logger)
    {
        _stats = stats;
        _aliveWorkers = aliveWorkers;
        _queueSize = queueSize;
        _logger = logger;
    }

    // Запустить цикл мониторинга. Работает, пока не отменят (CancellationToken).
    public async Task RunAsync(CancellationToken ct)
    {
        // Stopwatch — точный секундомер для измерения прошедшего времени.
        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Task.Delay — асинхронная пауза (не блокирует поток, в отличие от Thread.Sleep).
                await Task.Delay(1000, ct);

                double seconds = stopwatch.Elapsed.TotalSeconds;
                double rate = seconds > 0 ? _stats.PagesProcessed / seconds : 0;

                _logger.Info(
                    $"МОНИТОР: воркеров={_aliveWorkers()} | очередь={_queueSize()} | " +
                    $"обработано={_stats.PagesProcessed} | ошибок={_stats.PagesFailed} | " +
                    $"ссылок={_stats.LinksDiscovered} | скорость={rate:F1} стр/с");
            }
        }
        catch (OperationCanceledException)
        {
            // штатное завершение
        }
    }
}
