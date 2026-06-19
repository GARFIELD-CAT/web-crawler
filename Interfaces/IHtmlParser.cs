namespace DistributedWebCrawler.Interfaces;

/// <summary>
/// Интерфейс разбора (парсинга) HTML-страницы.
/// Вынесен в интерфейс, чтобы при желании заменить реализацию
/// (например, на более точный парсер), не трогая краулер.
/// </summary>
public interface IHtmlParser
{
    /// <summary>Извлечь заголовок страницы (тег &lt;title&gt;).</summary>
    string ExtractTitle(string html);

    /// <summary>Извлечь слова из текста страницы (для поискового индекса).</summary>
    IReadOnlyList<string> ExtractWords(string html);

    /// <summary>
    /// Извлечь ссылки со страницы и привести их к абсолютному виду.
    /// baseUrl нужен, чтобы превратить относительную ссылку (/page) в полную (https://site/page).
    /// </summary>
    IReadOnlyList<string> ExtractLinks(string html, string baseUrl);
}
