namespace DistributedWebCrawler.Monitoring;

/// <summary>
/// Сбор статистики работы системы.
///
/// Счётчики обновляются из множества потоков одновременно, поэтому используем
/// класс Interlocked — он выполняет операции "атомарно" (целиком, без гонок).
/// Если бы мы писали просто "_pages++", разные потоки могли бы затереть
/// результаты друг друга и счётчик стал бы неверным.
/// </summary>
public class Statistics
{
    private long _pagesProcessed;   // успешно обработано страниц
    private long _pagesFailed;      // не удалось обработать
    private long _bytesDownloaded;  // суммарно скачано байт
    private long _linksDiscovered;  // суммарно найдено ссылок

    /// <summary>Зафиксировать успешную обработку страницы.</summary>
    public void RecordSuccess(int bytes, int links)
    {
        Interlocked.Increment(ref _pagesProcessed);
        Interlocked.Add(ref _bytesDownloaded, bytes);
        Interlocked.Add(ref _linksDiscovered, links);
    }

    /// <summary>Зафиксировать неудачу.</summary>
    public void RecordFailure() => Interlocked.Increment(ref _pagesFailed);

    // Чтение тоже делаем через Interlocked.Read — чтобы гарантированно
    // получить корректное (не "наполовину записанное") значение.
    public long PagesProcessed => Interlocked.Read(ref _pagesProcessed);
    public long PagesFailed => Interlocked.Read(ref _pagesFailed);
    public long BytesDownloaded => Interlocked.Read(ref _bytesDownloaded);
    public long LinksDiscovered => Interlocked.Read(ref _linksDiscovered);
}
