using System.Collections.Concurrent;
using System.Net.Sockets;
using DistributedWebCrawler.Models;

namespace DistributedWebCrawler.Network;

// Все, что мастер знает про один подключенный рабочий узел (воркер):
public class WorkerInfo
{
    public string Id { get; }

    public TcpClient Connection { get; }

    // Поток для чтения/записи данных
    public NetworkStream Stream { get; }

    // Сколько задач воркер готов обрабатывать одновременно.
    public int MaxParallelism { get; set; } = 1;

    // "Замок" для отправки: не даёт двум потокам одновременно писать в один и тот же
    // сетевой поток (иначе байты разных сообщений перемешаются).
    public SemaphoreSlim SendLock { get; } = new(1, 1);

    // Задачи, отправленные этому воркеру и ещё не завершённые.
    // Ключ — URL. Если воркер "упадёт", эти задачи вернём в общую очередь.
    public ConcurrentDictionary<string, CrawlTask> InFlight { get; } = new();

    // Время последнего пульса
    private long _lastHeartbeatTicks;

    public WorkerInfo(string id, TcpClient connection)
    {
        Id = id;
        Connection = connection;
        Stream = connection.GetStream();
        // при создании считаем, что пульс только что был
        Touch();
    }

    // Сколько задач сейчас "в работе" у воркера
    public int InFlightCount => InFlight.Count;

    // Отметить, что от воркера только что пришёл "пульс" (он жив).
    public void Touch() => Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);

    // Время последнего полученного "пульса"
    public DateTime LastHeartbeat => new(Interlocked.Read(ref _lastHeartbeatTicks), DateTimeKind.Utc);
}
