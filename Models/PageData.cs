namespace DistributedWebCrawler.Models;

// Результат обработки одной веб-страницы рабочим узлом.
// Воркер скачивает страницу, разбирает её и заполняет этот объект,
// после чего отправляет его мастеру по сети.
public class PageData
{
    public string Url { get; set; } = string.Empty;
    public int Depth { get; set; }
    public string Title { get; set; } = string.Empty;
    public List<string> Words { get; set; } = new();
    public List<string> Links { get; set; } = new();
    public int ByteCount { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}
