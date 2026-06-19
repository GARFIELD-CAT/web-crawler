using System.Net.Sockets;
using DistributedWebCrawler.Crawling;
using DistributedWebCrawler.Interfaces;
using DistributedWebCrawler.Models;

namespace DistributedWebCrawler.Network;

/// <summary>
/// Рабочий узел (воркер).
///
/// Жизненный цикл:
///   1) подключается к мастеру по TCP и регистрируется;
///   2) периодически шлёт "пульс" (heartbeat), чтобы мастер знал, что воркер жив;
///   3) получает задачи (Assign), прогоняет их через конвейер Dataflow;
///   4) отправляет мастеру результат каждой обработанной страницы;
///   5) по команде Stop (или при обрыве связи) корректно завершается.
/// </summary>
public class WorkerClient
{
    private readonly string _masterHost;
    private readonly int _masterPort;
    private readonly string _workerId;
    private readonly int _maxParallelism;
    private readonly ILogger _logger;
    private readonly PageDownloader _downloader;
    private readonly IHtmlParser _parser;

    // Замок отправки: heartbeat и результаты шлются из разных мест,
    // нельзя писать в один сетевой поток одновременно.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private NetworkStream? _stream;
    private long _processed; // сколько страниц обработал (для статистики и heartbeat)

    public WorkerClient(
        string masterHost,
        int masterPort,
        string workerId,
        int maxParallelism,
        ILogger logger,
        PageDownloader downloader,
        IHtmlParser parser)
    {
        _masterHost = masterHost;
        _masterPort = masterPort;
        _workerId = workerId;
        _maxParallelism = maxParallelism;
        _logger = logger;
        _downloader = downloader;
        _parser = parser;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var client = new TcpClient();

        _logger.Info($"Подключаюсь к мастеру {_masterHost}:{_masterPort}...");
        await client.ConnectAsync(_masterHost, _masterPort, ct);
        _stream = client.GetStream();
        _logger.Info("Подключение установлено. Регистрируюсь у мастера.");

        // Сообщаем мастеру, кто мы и сколько задач тянем одновременно.
        await SendAsync(new Message
        {
            Type = MessageType.Register,
            WorkerId = _workerId,
            MaxParallelism = _maxParallelism
        }, ct);

        // Своя отмена: сработает либо при внешнем Ctrl+C, либо при команде Stop от мастера.
        using var internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken workCt = internalCts.Token;

        // Конвейер обработки. Результат каждой страницы сразу отправляем мастеру.
        var pipeline = new CrawlPipeline(
            _downloader,
            _parser,
            _maxParallelism,
            onResult: page =>
            {
                Interlocked.Increment(ref _processed);
                // Отправляем результат "не дожидаясь" (fire-and-forget), но через _sendLock,
                // поэтому сообщения не перемешаются. Ошибки внутри обрабатываются.
                _ = SendResultAsync(page, workCt);
            },
            workCt);

        // Фоновая задача "пульса".
        Task heartbeat = HeartbeatLoopAsync(workCt);

        try
        {
            // Главный цикл: читаем сообщения от мастера.
            while (!workCt.IsCancellationRequested)
            {
                Message? message = await MessageProtocol.ReceiveAsync(_stream, workCt);
                if (message is null)
                {
                    _logger.Warn("Мастер закрыл соединение.");
                    break;
                }

                switch (message.Type)
                {
                    case MessageType.Assign:
                        if (message.Task is not null)
                        {
                            _logger.Info($"Получена задача: {message.Task.Url} (глубина {message.Task.Depth})");
                            pipeline.Post(message.Task); // кладём в конвейер
                        }
                        break;

                    case MessageType.Stop:
                        _logger.Info("Получена команда остановки от мастера.");
                        internalCts.Cancel();
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // штатная остановка
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка в работе воркера", ex);
        }
        finally
        {
            internalCts.Cancel();
            try { await pipeline.CompleteAsync(); } catch { /* отмена — это нормально */ }
            try { await heartbeat; } catch { /* отмена — это нормально */ }
        }

        _logger.Info($"Воркер завершил работу. Обработано страниц: {Interlocked.Read(ref _processed)}");
    }

    /// <summary>Отправить мастеру результат обработки одной страницы.</summary>
    private async Task SendResultAsync(PageData page, CancellationToken ct)
    {
        try
        {
            await SendAsync(new Message
            {
                Type = MessageType.Result,
                WorkerId = _workerId,
                Result = page
            }, ct);
        }
        catch (OperationCanceledException)
        {
            // остановка — ничего страшного
        }
        catch (Exception ex)
        {
            _logger.Warn($"Не удалось отправить результат: {ex.Message}");
        }
    }

    /// <summary>Каждые 2 секунды шлём мастеру "пульс".</summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);
                await SendAsync(new Message
                {
                    Type = MessageType.Heartbeat,
                    WorkerId = _workerId,
                    Processed = Interlocked.Read(ref _processed)
                }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // штатная остановка
        }
        catch (Exception ex)
        {
            _logger.Warn($"Heartbeat остановлен: {ex.Message}");
        }
    }

    /// <summary>Потокобезопасная отправка сообщения мастеру.</summary>
    private async Task SendAsync(Message message, CancellationToken ct)
    {
        if (_stream is null)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            await MessageProtocol.SendAsync(_stream, message, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
