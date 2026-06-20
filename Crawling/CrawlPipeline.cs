using System.Threading.Tasks.Dataflow;
using DistributedWebCrawler.Interfaces;
using DistributedWebCrawler.Models;

namespace DistributedWebCrawler.Crawling;

// Конвейер обработки страниц на основе TPL Dataflow.
public class CrawlPipeline : IDisposable
{
    private readonly PageDownloader _downloader;
    private readonly IHtmlParser _parser;
    private readonly Action<PageData> _onResult;
    private readonly CancellationToken _ct;

    // Три блока конвейера:
    private readonly TransformBlock<CrawlTask, DownloadOutcome> _downloadBlock;
    private readonly TransformBlock<DownloadOutcome, PageData> _parseBlock;
    private readonly ActionBlock<PageData> _outputBlock;

    public CrawlPipeline(
        PageDownloader downloader,
        IHtmlParser parser,
        int maxParallelism,
        Action<PageData> onResult,
        CancellationToken ct)
    {
        _downloader = downloader;
        _parser = parser;
        _onResult = onResult;
        _ct = ct;

        // Настройки для блоков, которые работают параллельно.
        var parallelOptions = new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = maxParallelism, // сколько страниц обрабатывать одновременно
            CancellationToken = ct                   // конвейер можно отменить
        };

        // Для выдачи результата хватит одного потока (порядок результатов нам не важен).
        var singleThreadOptions = new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = 1,
            CancellationToken = ct
        };

        _downloadBlock = new TransformBlock<CrawlTask, DownloadOutcome>(DownloadAsync, parallelOptions);
        _parseBlock = new TransformBlock<DownloadOutcome, PageData>(Parse, parallelOptions);
        _outputBlock = new ActionBlock<PageData>(page => _onResult(page), singleThreadOptions);

        // Соединяем станции. PropagateCompletion = true означает:
        // когда предыдущий блок завершится, следующий тоже завершится (после обработки остатка).
        var linkOptions = new DataflowLinkOptions { PropagateCompletion = true };
        _downloadBlock.LinkTo(_parseBlock, linkOptions);
        _parseBlock.LinkTo(_outputBlock, linkOptions);
    }

    // Добавить задачу в конвейер (на вход первой станции).
    public void Post(CrawlTask task) => _downloadBlock.Post(task);

    // Сообщить, что новых задач больше не будет, и дождаться обработки всех уже добавленных.
    public async Task CompleteAsync()
    {
        _downloadBlock.Complete();        // вход закрыт
        await _outputBlock.Completion;    // ждём, пока всё дойдёт до последней станции
    }

    // 1: загрузка
    private async Task<DownloadOutcome> DownloadAsync(CrawlTask task)
    {
        (bool ok, string html, int bytes, string? error) = await _downloader.DownloadAsync(task.Url, _ct);
        return new DownloadOutcome { Task = task, Ok = ok, Html = html, Bytes = bytes, Error = error };
    }

    // 2: разбор HTML
    private PageData Parse(DownloadOutcome d)
    {
        if (!d.Ok)
        {
            // Загрузка не удалась — возвращаем результат-неудачу.
            return new PageData
            {
                Url = d.Task.Url,
                Depth = d.Task.Depth,
                Success = false,
                Error = d.Error,
                ByteCount = d.Bytes
            };
        }

        return new PageData
        {
            Url = d.Task.Url,
            Depth = d.Task.Depth,
            Success = true,
            Title = _parser.ExtractTitle(d.Html),
            Words = _parser.ExtractWords(d.Html).ToList(),
            Links = _parser.ExtractLinks(d.Html, d.Task.Url).ToList(),
            ByteCount = d.Bytes
        };
    }

    public void Dispose()
    {
    }

    // Промежуточный результат между станцией загрузки и станцией разбора.
    private class DownloadOutcome
    {
        public CrawlTask Task { get; set; } = null!;
        public bool Ok { get; set; }
        public string Html { get; set; } = string.Empty;
        public int Bytes { get; set; }
        public string? Error { get; set; }
    }
}
