using DistributedWebCrawler.Benchmark;
using DistributedWebCrawler.Crawling;
using DistributedWebCrawler.Distribution;
using DistributedWebCrawler.Interfaces;
using DistributedWebCrawler.Logging;
using DistributedWebCrawler.Network;

namespace DistributedWebCrawler;

/// <summary>
/// Точка входа в программу. В зависимости от первого аргумента запускается одна из ролей:
///   master    — узел-координатор (раздаёт задачи, собирает результаты);
///   worker    — рабочий узел (обходит страницы);
///   benchmark — сравнение последовательной и параллельной обработки.
///
/// Программа — ОДИН исполняемый файл, который умеет работать в разных ролях.
/// Это и реализует "мастер-рабочую" архитектуру: мастер и воркеры — отдельные
/// процессы (обычно в разных терминалах), общающиеся по TCP.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Корректная остановка по Ctrl+C: отменяем токен вместо аварийного завершения.
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // не убивать процесс мгновенно — дать коду завершиться красиво
            cts.Cancel();
            Console.WriteLine();
            Console.WriteLine("Получен Ctrl+C — останавливаюсь...");
        };

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        string role = args[0].ToLowerInvariant();
        try
        {
            return role switch
            {
                "master" => await RunMasterAsync(args, cts.Token),
                "worker" => await RunWorkerAsync(args, cts.Token),
                "benchmark" => await RunBenchmarkAsync(args, cts.Token),
                _ => UnknownRole()
            };
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Остановлено пользователем.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ОШИБКА: {ex.Message}");
            return 1;
        }
    }

    // ---------- Роли ----------

    private static async Task<int> RunMasterAsync(string[] args, CancellationToken ct)
    {
        int port = GetIntArg(args, "--port", 5000);
        string seed = GetArg(args, "--seed", "https://books.toscrape.com/");
        int depth = GetIntArg(args, "--depth", 2);
        int pages = GetIntArg(args, "--pages", 50);
        int delay = GetIntArg(args, "--delay", 200);
        string strategyName = GetArg(args, "--strategy", "roundrobin").ToLowerInvariant();
        // Пустое значение по умолчанию = имя файла сформируется автоматически
        // (хост сайта + дата и время обхода).
        string output = GetArg(args, "--output", "");

        ILogger logger = new ConsoleLogger("MASTER");

        // Выбор стратегии распределения (паттерн Strategy).
        IDistributionStrategy strategy = strategyName switch
        {
            "leastloaded" => new LeastLoadedStrategy(),
            _ => new RoundRobinStrategy()
        };

        var master = new MasterServer(port, logger, strategy, depth, pages, delay, output);
        await master.RunAsync(seed, ct);
        return 0;
    }

    private static async Task<int> RunWorkerAsync(string[] args, CancellationToken ct)
    {
        string master = GetArg(args, "--master", "localhost:5000");
        int parallelism = GetIntArg(args, "--parallelism", 8);
        // Если идентификатор не задан — генерируем уникальный (на основе номера процесса).
        string id = GetArg(args, "--id", $"worker-{Environment.ProcessId}");

        (string host, int port) = ParseHostPort(master);

        ILogger logger = new ConsoleLogger(id.ToUpperInvariant());
        using HttpClient http = CreateHttpClient();
        var downloader = new PageDownloader(http, BuildRetryPolicy(args, logger), logger);
        var parser = new HtmlParser();

        var worker = new WorkerClient(host, port, id, parallelism, logger, downloader, parser);
        await worker.RunAsync(ct);
        return 0;
    }

    private static async Task<int> RunBenchmarkAsync(string[] args, CancellationToken ct)
    {
        string seed = GetArg(args, "--seed", "https://books.toscrape.com/");
        int pages = GetIntArg(args, "--pages", 30);
        int parallelism = GetIntArg(args, "--parallelism", 8);

        ILogger logger = new ConsoleLogger("BENCH");
        using HttpClient http = CreateHttpClient();
        var downloader = new PageDownloader(http, BuildRetryPolicy(args, logger), logger);
        var parser = new HtmlParser();

        var benchmark = new BenchmarkRunner(downloader, parser, logger);
        await benchmark.RunAsync(seed, pages, parallelism, ct);
        return 0;
    }

    // ---------- Вспомогательные методы ----------

    /// <summary>Создать общий HttpClient с вежливыми настройками.</summary>
    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 64, // не открываем слишком много соединений к одному сайту
            AllowAutoRedirect = true
        };
        var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15) // не ждём вечно "зависшую" страницу
        };
        // Честно представляемся серверу (хороший тон для краулера).
        // ВАЖНО: значение заголовка должно быть только из ASCII-символов
        // (кириллица в HTTP-заголовках недопустима — иначе запрос упадёт).
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EducationalCrawler/1.0 (+educational-project)");
        return http;
    }

    /// <summary>Получить строковый аргумент вида "--name значение" или значение по умолчанию.</summary>
    private static string GetArg(string[] args, string name, string defaultValue)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return defaultValue;
    }

    /// <summary>То же, но для целочисленных аргументов.</summary>
    private static int GetIntArg(string[] args, string name, int defaultValue)
    {
        string value = GetArg(args, name, defaultValue.ToString());
        return int.TryParse(value, out int result) ? result : defaultValue;
    }

    /// <summary>
    /// Построить политику ретраев из аргументов:
    ///   --retries       сколько повторов (по умолчанию 2; 0 — без повторов)
    ///   --retry-delay   базовая пауза в мс (по умолчанию 500, дальше удваивается)
    ///   --retry-status  какие статусы повторять, через запятую (например "500,502,503");
    ///                   если не задано — берётся набор по умолчанию.
    /// Таймаут и сетевые сбои повторяются всегда, 404 и прочие — нет.
    /// </summary>
    private static RetryPolicy BuildRetryPolicy(string[] args, ILogger logger)
    {
        int retries = GetIntArg(args, "--retries", 2);
        int retryDelay = GetIntArg(args, "--retry-delay", 500);
        IEnumerable<int>? statuses = ParseStatusCodes(GetArg(args, "--retry-status", string.Empty));

        var policy = new RetryPolicy(retries, TimeSpan.FromMilliseconds(retryDelay), statuses);
        logger.Info($"Ретраи: повторов={retries}, базовая пауза={retryDelay} мс, статусы=[{policy.DescribeStatuses()}] (+ таймаут/сеть)");
        return policy;
    }

    /// <summary>Разобрать список HTTP-статусов "500,502,503". Пусто => null (взять набор по умолчанию).</summary>
    private static IEnumerable<int>? ParseStatusCodes(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var codes = new List<int>();
        foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out int code))
                codes.Add(code);
        }
        return codes.Count > 0 ? codes : null;
    }

    /// <summary>Разобрать строку "host:port" на хост и порт.</summary>
    private static (string Host, int Port) ParseHostPort(string value)
    {
        string[] parts = value.Split(':');
        string host = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : "localhost";
        int port = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : 5000;
        return (host, port);
    }

    private static int UnknownRole()
    {
        Console.WriteLine("Неизвестная роль.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Распределённый веб-краулер. Использование:");
        Console.WriteLine();
        Console.WriteLine("  Мастер (координатор):");
        Console.WriteLine("    dotnet run -- master [--port 5000] [--seed URL] [--depth 2] [--pages 50]");
        Console.WriteLine("                        [--strategy roundrobin|leastloaded] [--delay 200]");
        Console.WriteLine("                        [--output файл.csv]   (по умолчанию: хост_дата-время.csv)");
        Console.WriteLine();
        Console.WriteLine("  Воркер (рабочий узел):");
        Console.WriteLine("    dotnet run -- worker [--master localhost:5000] [--parallelism 8] [--id worker-1]");
        Console.WriteLine("                        [--retries 2] [--retry-delay 500] [--retry-status 500,502,503,504]");
        Console.WriteLine();
        Console.WriteLine("  Бенчмарк (сравнение последовательной и параллельной обработки):");
        Console.WriteLine("    dotnet run -- benchmark [--seed URL] [--pages 30] [--parallelism 8]");
        Console.WriteLine("                           [--retries 2] [--retry-delay 500] [--retry-status 500,502,503,504]");
    }
}
