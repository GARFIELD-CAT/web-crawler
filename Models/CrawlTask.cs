namespace DistributedWebCrawler.Models;

// Одна задача для краулера: какой URL нужно загрузить и на какой глубине
// он находится относительно стартовой страницы
public class CrawlTask
{
    public string Url { get; set; } = string.Empty;
    public int Depth { get; set; }
    public CrawlTask() { }

    public CrawlTask(string url, int depth)
    {
        Url = url;
        Depth = depth;
    }
}
