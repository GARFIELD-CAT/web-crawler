namespace DistributedWebCrawler.Monitoring;

// Сбор статистики работы системы.
public class Statistics
{
    private long _pagesProcessed;   // успешно обработано страниц
    private long _pagesFailed;      // не удалось обработать
    private long _bytesDownloaded;  // суммарно скачано байт
    private long _linksDiscovered;  // суммарно найдено ссылок

    // Зафиксировать успешную обработку страницы
    public void RecordSuccess(int bytes, int links)
    {
        Interlocked.Increment(ref _pagesProcessed);
        Interlocked.Add(ref _bytesDownloaded, bytes);
        Interlocked.Add(ref _linksDiscovered, links);
    }

    // Зафиксировать неудачу.
    public void RecordFailure() => Interlocked.Increment(ref _pagesFailed);
    public long PagesProcessed => Interlocked.Read(ref _pagesProcessed);
    public long PagesFailed => Interlocked.Read(ref _pagesFailed);
    public long BytesDownloaded => Interlocked.Read(ref _bytesDownloaded);
    public long LinksDiscovered => Interlocked.Read(ref _linksDiscovered);
}
