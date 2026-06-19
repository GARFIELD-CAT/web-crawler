using System.Net;
using System.Text.RegularExpressions;
using DistributedWebCrawler.Interfaces;

namespace DistributedWebCrawler.Crawling;

/// <summary>
/// Разбирает HTML "вручную" с помощью регулярных выражений.
///
/// ВАЖНО (для защиты у преподавателя): по-настоящему правильно HTML парсят
/// специальными библиотеками (например, AngleSharp или HtmlAgilityPack), но они
/// внешние, а задание запрещает сторонние библиотеки. Поэтому здесь мы используем
/// регулярные выражения из стандартной библиотеки .NET. Для учебного примера и
/// аккуратной вёрстки этого достаточно, но для "боевого" краулера так делать не стоит.
/// </summary>
public class HtmlParser : IHtmlParser
{
    // Регулярные выражения компилируем один раз (RegexOptions.Compiled) — так быстрее,
    // потому что они применяются к каждой странице.

    // Заголовок: содержимое <title>...</title>
    private static readonly Regex TitleRegex =
        new("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // Любой HTML-тег (для удаления разметки и получения чистого текста)
    private static readonly Regex TagRegex =
        new("<[^>]+>", RegexOptions.Compiled);

    // Блоки <script>...</script> и <style>...</style> вместе с содержимым (его не индексируем)
    private static readonly Regex ScriptStyleRegex =
        new("<(script|style)[^>]*>.*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // Значение href у ссылок <a href="...">. Отбрасываем якоря (#...).
    private static readonly Regex HrefRegex =
        new("<a\\s[^>]*?href\\s*=\\s*[\"']([^\"'#]+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "Слово" — последовательность букв и цифр (поддерживает Unicode, т.е. и русские буквы).
    private static readonly Regex WordRegex =
        new(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);

    public string ExtractTitle(string html)
    {
        Match match = TitleRegex.Match(html);
        // WebUtility.HtmlDecode превращает HTML-сущности (&amp; и т.п.) в обычные символы.
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : "(без заголовка)";
    }

    public IReadOnlyList<string> ExtractWords(string html)
    {
        // 1) убираем скрипты и стили, 2) убираем все теги, 3) раскодируем сущности
        string noScripts = ScriptStyleRegex.Replace(html, " ");
        string text = WebUtility.HtmlDecode(TagRegex.Replace(noScripts, " "));

        var words = new List<string>();
        foreach (Match match in WordRegex.Matches(text))
        {
            string word = match.Value.ToLowerInvariant();
            if (word.Length >= 3) // слишком короткие токены ("a", "of") не индексируем
                words.Add(word);
        }
        return words;
    }

    public IReadOnlyList<string> ExtractLinks(string html, string baseUrl)
    {
        var result = new List<string>();

        // Базовый адрес нужен, чтобы относительные ссылки превратить в абсолютные.
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri))
            return result;

        foreach (Match match in HrefRegex.Matches(html))
        {
            string href = match.Groups[1].Value.Trim();

            // Uri.TryCreate(baseUri, href, ...) корректно склеивает базовый адрес и ссылку.
            if (Uri.TryCreate(baseUri, href, out Uri? abs) &&
                (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
            {
                // GetLeftPart(Path) отбрасывает "?query" и "#fragment" — нормализуем адрес,
                // чтобы не считать https://site/a и https://site/a?x=1 разными страницами.
                result.Add(abs.GetLeftPart(UriPartial.Path));
            }
        }
        return result;
    }
}
