using System.Collections.Concurrent;
using System.Net.Sockets;
using DistributedWebCrawler.Models;

namespace DistributedWebCrawler.Network;

/// <summary>
/// Всё, что мастер знает про один подключённый рабочий узел (воркер):
/// его идентификатор, сетевое соединение, время последнего "пульса"
/// и список выданных, но ещё не выполненных задач.
/// Объект живёт на стороне мастера и НЕ передаётся по сети.
/// </summary>
public class WorkerInfo
{
    /// <summary>Уникальный идентификатор воркера (приходит в сообщении Register).</summary>
    public string Id { get; }

    /// <summary>TCP-соединение с воркером.</summary>
    public TcpClient Connection { get; }

    /// <summary>Поток для чтения/записи данных по этому соединению.</summary>
    public NetworkStream Stream { get; }

    /// <summary>Сколько задач воркер готов обрабатывать одновременно.</summary>
    public int MaxParallelism { get; set; } = 1;

    /// <summary>
    /// "Замок" для отправки: не даёт двум потокам одновременно писать в один и тот же
    /// сетевой поток (иначе байты разных сообщений перемешаются).
    /// SemaphoreSlim — современный асинхронный примитив (не устаревший Monitor).
    /// </summary>
    public SemaphoreSlim SendLock { get; } = new(1, 1);

    /// <summary>
    /// Задачи, отправленные этому воркеру и ещё не завершённые.
    /// Ключ — URL. Если воркер "упадёт", эти задачи вернём в общую очередь.
    /// ConcurrentDictionary — потокобезопасный словарь.
    /// </summary>
    public ConcurrentDictionary<string, CrawlTask> InFlight { get; } = new();

    // Время последнего "пульса" в виде тиков (мельчайших единиц времени).
    // Храним как long-поле, чтобы читать/писать его атомарно через Interlocked.
    private long _lastHeartbeatTicks;

    public WorkerInfo(string id, TcpClient connection)
    {
        Id = id;
        Connection = connection;
        Stream = connection.GetStream();
        Touch(); // при создании считаем, что пульс только что был
    }

    /// <summary>Сколько задач сейчас "в работе" у воркера (для балансировки нагрузки).</summary>
    public int InFlightCount => InFlight.Count;

    /// <summary>
    /// Отметить, что от воркера только что пришёл "пульс" (он жив).
    /// Interlocked.Exchange атомарно записывает новое значение.
    /// </summary>
    public void Touch() => Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);

    /// <summary>Время последнего полученного "пульса".</summary>
    public DateTime LastHeartbeat => new(Interlocked.Read(ref _lastHeartbeatTicks), DateTimeKind.Utc);
}
