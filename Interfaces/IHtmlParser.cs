namespace DistributedWebCrawler.Interfaces;

// Интерфейс разбора (парсинга) HTML-страницы.
public interface IHtmlParser
{
    // Извлечь заголовок страницы (тег &lt;title&gt;).
    string ExtractTitle(string html);

    // Извлечь слова из текста страницы (для поискового индекса).
    IReadOnlyList<string> ExtractWords(string html);

    // Извлечь ссылки со страницы и привести их к абсолютному виду.
    IReadOnlyList<string> ExtractLinks(string html, string baseUrl);
}
