using System.Collections.Concurrent;
using System.Diagnostics;
using DistributedWebCrawler.Crawling;
using DistributedWebCrawler.Interfaces;
using DistributedWebCrawler.Models;

namespace DistributedWebCrawler.Benchmark;

// Сравнивает два способа обработки одного и того же набора страниц:
//   1) последовательно (по одной странице за раз);
//   2) параллельно (через конвейер TPL Dataflow).
public class BenchmarkRunner
{
    private readonly PageDownloader _downloader;
    private readonly IHtmlParser _parser;
    private readonly ILogger _logger;

    public BenchmarkRunner(PageDownloader downloader, IHtmlParser parser, ILogger logger)
    {
        _downloader = downloader;
        _parser = parser;
        _logger = logger;
    }

    public async Task RunAsync(string seedUrl, int pageCount, int maxParallelism, CancellationToken ct)
    {
        _logger.Info($"Бенчмарк: собираю ~{pageCount} URL со стартовой страницы {seedUrl}...");
        List<string> urls = await CollectUrlsAsync(seedUrl, pageCount, ct);
        _logger.Info($"Собрано URL: {urls.Count}. Параллелизм: {maxParallelism}.");

        if (urls.Count == 0)
        {
            _logger.Error("Не удалось собрать URL для бенчмарка (проверьте доступность сайта).");
            return;
        }

        // ---------- 1) Последовательная обработка ----------
        _logger.Info("Запуск ПОСЛЕДОВАТЕЛЬНОЙ обработки...");
        var stopwatch = Stopwatch.StartNew();
        int sequentialOk = 0;
        foreach (string url in urls)
        {
            (bool ok, string html, _, _) = await _downloader.DownloadAsync(url, ct);
            if (ok)
            {
                // Делаем ту же работу, что и параллельный вариант, чтобы сравнение было честным.
                _parser.ExtractTitle(html);
                _parser.ExtractWords(html);
                _parser.ExtractLinks(html, url);
                sequentialOk++;
            }
        }
        stopwatch.Stop();
        TimeSpan sequentialTime = stopwatch.Elapsed;
        _logger.Info($"Последовательно: {sequentialOk} страниц за {sequentialTime.TotalSeconds:F2} с");

        // ---------- 2) Параллельная обработка (TPL Dataflow) ----------
        _logger.Info("Запуск ПАРАЛЛЕЛЬНОЙ обработки...");
        var results = new ConcurrentBag<PageData>();
        stopwatch.Restart();
        using (var pipeline = new CrawlPipeline(_downloader, _parser, maxParallelism, p => results.Add(p), ct))
        {
            foreach (string url in urls)
                pipeline.Post(new CrawlTask(url, 0));
            await pipeline.CompleteAsync();
        }
        stopwatch.Stop();
        TimeSpan parallelTime = stopwatch.Elapsed;
        int parallelOk = results.Count(p => p.Success);
        _logger.Info($"Параллельно: {parallelOk} страниц за {parallelTime.TotalSeconds:F2} с");

        // ---------- Подсчёт показателей ----------
        double speedup = parallelTime.TotalSeconds > 0
            ? sequentialTime.TotalSeconds / parallelTime.TotalSeconds
            : 0;
        double efficiency = maxParallelism > 0 ? speedup / maxParallelism : 0;

        PrintTable(urls.Count, maxParallelism, sequentialTime, parallelTime, speedup, efficiency);
    }

    // Собрать набор URL для теста
    private async Task<List<string>> CollectUrlsAsync(string seed, int target, CancellationToken ct)
    {
        if (!Uri.TryCreate(seed, UriKind.Absolute, out Uri? seedUri))
            return new List<string>();

        string host = seedUri.Host;
        var collected = new List<string> { seed };
        var seen = new HashSet<string> { seed };
        var queue = new Queue<string>();
        queue.Enqueue(seed);

        while (collected.Count < target && queue.Count > 0 && !ct.IsCancellationRequested)
        {
            string url = queue.Dequeue();
            (bool ok, string html, _, _) = await _downloader.DownloadAsync(url, ct);
            if (!ok)
                continue;

            foreach (string link in _parser.ExtractLinks(html, url))
            {
                if (!Uri.TryCreate(link, UriKind.Absolute, out Uri? linkUri) || linkUri.Host != host)
                    continue;

                if (seen.Add(link))
                {
                    collected.Add(link);
                    queue.Enqueue(link);
                    if (collected.Count >= target)
                        break;
                }
            }
        }

        return collected.Take(target).ToList();
    }

    private void PrintTable(int pages, int parallelism, TimeSpan seq, TimeSpan par, double speedup, double efficiency)
    {
        _logger.Info("==================== РЕЗУЛЬТАТЫ БЕНЧМАРКА ====================");
        _logger.Info($"Страниц обработано:       {pages}");
        _logger.Info($"Степень параллелизма:     {parallelism}");
        _logger.Info($"Последовательно:          {seq.TotalSeconds:F2} с");
        _logger.Info($"Параллельно (Dataflow):   {par.TotalSeconds:F2} с");
        _logger.Info($"Ускорение (speedup):      x{speedup:F2}");
        _logger.Info($"Эффективность:            {efficiency * 100:F1} %");
        _logger.Info("=============================================================");
    }
}
