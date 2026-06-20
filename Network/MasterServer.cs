using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DistributedWebCrawler.Export;
using DistributedWebCrawler.Indexing;
using DistributedWebCrawler.Interfaces;
using DistributedWebCrawler.Models;
using DistributedWebCrawler.Monitoring;

namespace DistributedWebCrawler.Network;

// Мастер-узел распределённой системы.
//
// Обязанности:
//   • принимать TCP-подключения воркеров;
//   • хранить очередь URL для обхода (frontier) и список уже виденных адресов;
//   • раздавать задачи воркерам по выбранной стратегии;
//   • получать результаты, складывать данные в индекс и ставить новые ссылки в очередь;
//   • следить за "пульсом" воркеров и переназначать задачи упавших узлов (отказоустойчивость);
//   • показывать статистику и в конце дать поиск по собранным данным.
//
// Координация идёт через объекты-поля этого класса (очереди, словари)
public sealed class MasterServer
{
    private readonly int _port;
    private readonly ILogger _logger;
    private readonly IDistributionStrategy _strategy;
    private readonly int _maxDepth;
    private readonly int _maxPages;
    private readonly int _politenessDelayMs;
    private readonly string _outputPath;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly int _heartbeatCheckIntervalMs;

    // Список подключённых воркеров: Id -> информация о воркере.
    private readonly ConcurrentDictionary<string, WorkerInfo> _workers = new();

    // Очередь задач. BlockingCollection потокобезопасна и умеет "ждать", пока в очереди не появятся элементы
    private readonly BlockingCollection<CrawlTask> _frontier = new();

    // Множество уже увиденных URL (чтобы не обходить одну страницу дважды).
    private readonly ConcurrentDictionary<string, byte> _visited = new();

    // Множество уже обработанных URL (по которым пришёл и учтён результат).
    private readonly ConcurrentDictionary<string, byte> _completed = new();

    private readonly InvertedIndex _index = new();
    private readonly Statistics _stats = new();
    private readonly SystemMonitor _monitor;
    private readonly CsvExporter _csvExporter;

    // Все полученные результаты (и успешные, и с ошибкой) — для выгрузки в CSV в конце.
    private readonly ConcurrentQueue<PageData> _allResults = new();

    // Счётчики (меняем атомарно через Interlocked):
    private long _pendingCount;    // сколько задач "в работе" (в очереди или у воркеров)
    private long _enqueuedTotal;   // сколько всего задач поставлено в очередь (для лимита страниц)
    private long _pagesIndexed;    // сколько страниц успешно проиндексировано
    private long _notFoundCount;   // сколько ссылок вернули 404 (страница не найдена)

    // Несколько примеров ссылок с 404
    private readonly ConcurrentQueue<string> _notFoundSamples = new();

    // Сигнал "обход полностью завершён".
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _seedHost = string.Empty; // домен стартовой страницы (краулим только его)

    public MasterServer(
        int port,
        ILogger logger,
        IDistributionStrategy strategy,
        int maxDepth,
        int maxPages,
        int politenessDelayMs,
        string outputPath)
    {
        _port = port;
        _logger = logger;
        _strategy = strategy;
        _maxDepth = maxDepth;
        _maxPages = maxPages;
        _politenessDelayMs = politenessDelayMs;
        _outputPath = outputPath;
        _csvExporter = new CsvExporter(logger);
        _heartbeatTimeout = TimeSpan.FromSeconds(6);   // нет пульса 6 секунд -> считаем упавшим
        _heartbeatCheckIntervalMs = 2000;              // проверяем пульс каждые 2 секунды

        // Монитору даём функции, которыми он будет узнавать состояние системы.
        _monitor = new SystemMonitor(
            _stats,
            aliveWorkers: () => GetAliveWorkers().Count,
            queueSize: () => _frontier.Count + InFlightTotal(),
            logger);
    }

    // Запустить мастер: слушать порт и обходить сайт, начиная с seedUrl.
    public async Task RunAsync(string seedUrl, CancellationToken externalCt)
    {
        // Связываем нашу отмену с внешней (Ctrl+C). Любая из них остановит систему.
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        CancellationToken ct = _cts.Token;

        _seedHost = new Uri(seedUrl).Host;

        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _logger.Info($"Мастер слушает порт {_port}. Домен обхода: {_seedHost}");
        _logger.Info($"Стартовый URL: {seedUrl} | максимальная глубина: {_maxDepth} | лимит страниц: {_maxPages}");
        _logger.Info($"Стратегия распределения: {_strategy.Name}");
        _logger.Info("Жду подключения воркеров... Запустите воркер в другом терминале:");
        _logger.Info($"    dotnet run -- worker --master localhost:{_port}");

        var stopwatch = Stopwatch.StartNew();

        // Запускаем фоновые задачи. Каждая работает в своём асинхронном цикле.
        Task acceptLoop = AcceptLoopAsync(ct);                 // приём подключений
        Task dispatchLoop = Task.Run(() => DispatchLoopAsync(ct)); // раздача задач
        Task heartbeatLoop = HeartbeatMonitorAsync(ct);        // контроль "пульса"
        Task monitorLoop = _monitor.RunAsync(ct);              // вывод статистики

        // Кладём стартовый URL в очередь — отсюда начинается обход.
        TryEnqueue(new CrawlTask(seedUrl, 0));

        // Ждём, пока обход завершится (очередь опустеет) ИЛИ нас отменят (Ctrl+C).
        await Task.WhenAny(_completion.Task, WaitForCancellationAsync(ct));
        stopwatch.Stop();

        _logger.Info("Останавливаю систему...");
        _cts!.Cancel();

        // Просим воркеров корректно завершиться.
        foreach (WorkerInfo worker in _workers.Values)
            await SendToWorkerAsync(worker, new Message { Type = MessageType.Stop }, CancellationToken.None);

        _listener?.Stop();

        PrintSummary(stopwatch.Elapsed);

        // Выгружаем собранные данные в CSV-файл.
        try
        {
            _csvExporter.Export(ResolveOutputPath(), _allResults);
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось выгрузить данные в CSV", ex);
        }

        RunInteractiveSearch(externalCt);
    }

    // Приём подключений

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break; // нас остановили
            }
            catch (Exception ex)
            {
                _logger.Error("Ошибка при приёме подключения", ex);
                continue;
            }

            // Каждого воркера обслуживаем в отдельной задаче, чтобы не блокировать приём новых.
            _ = HandleWorkerAsync(client, ct);
        }
    }

    // Цикл обслуживания одного воркера: читаем его сообщения и реагируем.
    private async Task HandleWorkerAsync(TcpClient client, CancellationToken ct)
    {
        WorkerInfo? worker = null;
        try
        {
            NetworkStream stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                Message? message = await MessageProtocol.ReceiveAsync(stream, ct);
                if (message is null)
                    break; // воркер отключился

                switch (message.Type)
                {
                    case MessageType.Register:
                        worker = new WorkerInfo(message.WorkerId, client) { MaxParallelism = message.MaxParallelism };
                        _workers[worker.Id] = worker;
                        _logger.Info($"Воркер подключился: {worker.Id} (параллелизм {worker.MaxParallelism})");
                        break;

                    case MessageType.Heartbeat:
                        worker?.Touch(); // отметили, что воркер жив
                        break;

                    case MessageType.Result:
                        if (worker is not null && message.Result is not null)
                            ProcessResult(worker, message.Result);
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
            _logger.Warn($"Соединение с воркером прервано: {ex.Message}");
        }
        finally
        {
            if (worker is not null)
                RemoveWorker(worker, "соединение закрыто");
            client.Dispose();
        }
    }

    // Раздача задач

    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        try
        {
            // GetConsumingEnumerable выдаёт задачи по мере появления
            foreach (CrawlTask task in _frontier.GetConsumingEnumerable(ct))
            {
                WorkerInfo? worker = await WaitForWorkerAsync(ct);
                if (worker is null)
                    break; // нас отменили

                // Помечаем задачу как выданную этому воркеру (понадобится при сбое).
                worker.InFlight[task.Url] = task;
                await SendToWorkerAsync(worker, new Message { Type = MessageType.Assign, Task = task }, ct);

                // Небольшая пауза между выдачами задач, чтобы не отправлять слишком много запросов разом
                if (_politenessDelayMs > 0)
                    await Task.Delay(_politenessDelayMs, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // штатная остановка
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка в цикле раздачи задач", ex);
        }
    }

    // Ждать, пока появится хотя бы один живой воркер, и вернуть выбранного стратегией.
    private async Task<WorkerInfo?> WaitForWorkerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            WorkerInfo? worker = _strategy.SelectWorker(GetAliveWorkers());
            if (worker is not null)
                return worker;

            await Task.Delay(100, ct); // воркеров пока нет — немного подождём
        }
        return null;
    }

    // Обработка результата

    private void ProcessResult(WorkerInfo worker, PageData page)
    {
        // Задача больше не "в работе" у этого воркера.
        worker.InFlight.TryRemove(page.Url, out _);

        // Защита от повторной обработки одного и того же URL. Дубликат может прийти
        if (!_completed.TryAdd(page.Url, 0))
            return;

        if (page.Success)
        {
            // Для выгрузки в CSV сохраняем только успешно обработанные страницы.
            _allResults.Enqueue(page);

            _index.AddDocument(page);
            Interlocked.Increment(ref _pagesIndexed);
            _stats.RecordSuccess(page.ByteCount, page.Links.Count);

            // Ставим в очередь найденные ссылки (они на одну глубину дальше).
            foreach (string link in page.Links)
                TryEnqueue(new CrawlTask(link, page.Depth + 1));
        }
        else
        {
            _stats.RecordFailure();

            //Игнорируем ошибки 404 (страница не найдена). И пару примеров выводим
            bool isNotFound = page.Error is not null && page.Error.Contains("404");
            if (isNotFound)
            {
                Interlocked.Increment(ref _notFoundCount);
                if (_notFoundSamples.Count < 10)
                    _notFoundSamples.Enqueue(page.Url);
            }
            else
            {
                // Остальные (более редкие и интересные) ошибки по-прежнему пишем в лог.
                _logger.Warn($"Не удалось обработать {page.Url}: {page.Error}");
            }
        }

        // Эта задача завершена. Если незавершённых задач не осталось — обход окончен.
        if (Interlocked.Decrement(ref _pendingCount) == 0)
            SignalCompletion();
    }

    // Попытаться добавить задачу в очередь.
    private bool TryEnqueue(CrawlTask task)
    {
        if (task.Depth > _maxDepth)
            return false;

        if (!IsSameDomain(task.Url))
            return false;

        // TryAdd вернёт false, если URL уже есть в множестве (значит, уже видели).
        if (!_visited.TryAdd(task.Url, 0))
            return false;

        // Лимит на общее число страниц. Increment атомарно увеличивает и возвращает новое значение.
        if (Interlocked.Increment(ref _enqueuedTotal) > _maxPages)
            return false; // лимит достигнут — больше не добавляем

        Interlocked.Increment(ref _pendingCount);

        // Очередь уже закрыта (обход завершается) — не добавляем.
        if (_frontier.IsAddingCompleted)
        {
            Interlocked.Decrement(ref _pendingCount);
            return false;
        }

        try
        {
            _frontier.Add(task);
        }
        catch (InvalidOperationException)
        {
            // Очередь закрылась между проверкой и добавлением — редкий случай, просто игнорируем.
            Interlocked.Decrement(ref _pendingCount);
            return false;
        }
        return true;
    }

    // Контроль "пульса" и отказоустойчивость

    private async Task HeartbeatMonitorAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_heartbeatCheckIntervalMs, ct);

                DateTime now = DateTime.UtcNow;
                foreach (WorkerInfo worker in _workers.Values)
                {
                    if (now - worker.LastHeartbeat > _heartbeatTimeout)
                        RemoveWorker(worker, "нет heartbeat (узел считается упавшим)");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // штатная остановка
        }
    }

    // Удалить воркера и вернуть его незавершённые задачи в очередь
    private void RemoveWorker(WorkerInfo worker, string reason)
    {
        // TryRemove гарантирует, что обработаем удаление только один раз
        if (!_workers.TryRemove(worker.Id, out _))
            return;

        int reassigned = worker.InFlightCount;
        _logger.Warn($"Воркер {worker.Id} удалён: {reason}. Возвращаю в очередь задач: {reassigned}.");

        foreach (KeyValuePair<string, CrawlTask> entry in worker.InFlight)
        {
            // Эти задачи уже учтены в _pendingCount (результат по ним не пришёл)
            if (!_frontier.IsAddingCompleted)
            {
                try { _frontier.Add(entry.Value); }
                catch (InvalidOperationException) { /* очередь закрыта — игнорируем */ }
            }
        }

        worker.InFlight.Clear();
        try { worker.Connection.Dispose(); } catch { /* уже закрыто */ }
    }

    // Вспомогательные методы 

    private IReadOnlyCollection<WorkerInfo> GetAliveWorkers()
    {
        DateTime now = DateTime.UtcNow;
        // Живой воркер = тот, от которого недавно был "пульс".
        return _workers.Values
            .Where(w => now - w.LastHeartbeat <= _heartbeatTimeout)
            .ToList();
    }

    private int InFlightTotal() => _workers.Values.Sum(w => w.InFlightCount);

    private bool IsSameDomain(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? u) &&
        string.Equals(u.Host, _seedHost, StringComparison.OrdinalIgnoreCase);

    private async Task SendToWorkerAsync(WorkerInfo worker, Message message, CancellationToken ct)
    {
        // SendLock не даёт двум потокам одновременно писать в один сетевой поток.
        await worker.SendLock.WaitAsync(ct);
        try
        {
            await MessageProtocol.SendAsync(worker.Stream, message, ct);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Не удалось отправить сообщение воркеру {worker.Id}: {ex.Message}");
        }
        finally
        {
            worker.SendLock.Release();
        }
    }

    private void SignalCompletion()
    {
        _frontier.CompleteAdding();       // больше задач не будет — цикл раздачи завершится
        _completion.TrySetResult();       // сигналим, что обход окончен
    }

    // Превращает отмену токена в Task, который "завершится" при отмене.
    private static Task WaitForCancellationAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetResult());
        return tcs.Task;
    }

    // Определить путь к CSV-файлу. Если пользователь задал --output, используем его.
    // Иначе формируем имя автоматически: "хост_дата-время.csv"
    private string ResolveOutputPath()
    {
        if (!string.IsNullOrWhiteSpace(_outputPath))
            return _outputPath;

        string host = string.IsNullOrEmpty(_seedHost) ? "site" : _seedHost;
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return $"{SanitizeFileName(host)}_{timestamp}.csv";
    }

    // Убрать из имени файла символы, недопустимые в именах файлов.
    private static string SanitizeFileName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name;
    }

    private void PrintSummary(TimeSpan elapsed)
    {
        double seconds = elapsed.TotalSeconds;
        double rate = seconds > 0 ? _pagesIndexed / seconds : 0;

        _logger.Info("==================== ИТОГИ ОБХОДА ====================");
        _logger.Info($"Время работы:           {elapsed.TotalSeconds:F1} с");
        _logger.Info($"Проиндексировано:       {_pagesIndexed} страниц");
        _logger.Info($"Ошибок обработки:       {_stats.PagesFailed}");
        _logger.Info($"   из них 404 (нет страницы): {Interlocked.Read(ref _notFoundCount)}");
        _logger.Info($"Найдено ссылок:         {_stats.LinksDiscovered}");
        _logger.Info($"Скачано данных:         {_stats.BytesDownloaded / 1024.0:F1} КБ");
        _logger.Info($"Уникальных слов в индексе: {_index.TermCount}");
        _logger.Info($"Средняя скорость:       {rate:F1} страниц/с");
        _logger.Info("======================================================");

        // Несколько примеров ссылок, вернувших 404 (если такие были).
        if (!_notFoundSamples.IsEmpty)
        {
            _logger.Info("Примеры ссылок с ошибкой 404 (до 10):");
            foreach (string url in _notFoundSamples)
                _logger.Info($"   {url}");
        }
    }

    // Простой интерактивный поиск по собранному индексу.
    private void RunInteractiveSearch(CancellationToken ct)
    {
        if (ct.IsCancellationRequested || _index.DocumentCount == 0)
            return;

        _logger.Info("Поиск по собранным данным (пустая строка — выход).");
        _logger.Info("  слова       — найти страницы с любым из слов;");
        _logger.Info("  \"точная фраза\" в кавычках — найти страницы, где слова идут подряд.");
        while (!ct.IsCancellationRequested)
        {
            Console.Write("поиск> ");
            string? query = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(query))
                break;

            query = query.Trim();

            // Запрос в двойных кавычках => поиск по точной фразе, иначе обычный поиск по словам.
            IReadOnlyList<SearchResult> results =
                query.Length >= 2 && query.StartsWith('"') && query.EndsWith('"')
                    ? _index.SearchPhrase(query.Trim('"'), maxResults: 10)
                    : _index.Search(query, maxResults: 10);

            if (results.Count == 0)
            {
                Console.WriteLine("  Ничего не найдено.");
                continue;
            }

            int position = 1;
            foreach (SearchResult result in results)
            {
                Console.WriteLine($"  {position++}. [{result.Score}] {result.Title}");
                Console.WriteLine($"     {result.Url}");
            }
        }
    }
}