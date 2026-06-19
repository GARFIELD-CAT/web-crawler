namespace DistributedWebCrawler.Models;

/// <summary>
/// Одна задача для краулера: какой URL нужно загрузить и на какой "глубине"
/// он находится относительно стартовой страницы.
/// Глубина 0 — это стартовая страница, 1 — страницы по ссылкам с неё, и так далее.
/// Класс простой и неизменяемый по смыслу (мы его не меняем после создания).
/// </summary>
public class CrawlTask
{
    /// <summary>Полный (абсолютный) адрес страницы, например https://site/page.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Глубина: сколько "переходов по ссылкам" от старта до этой страницы.</summary>
    public int Depth { get; set; }

    // Пустой конструктор нужен для десериализации из JSON (когда задача приходит по сети).
    public CrawlTask() { }

    public CrawlTask(string url, int depth)
    {
        Url = url;
        Depth = depth;
    }
}
