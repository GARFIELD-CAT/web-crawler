using System.Text;

namespace DistributedWebCrawler.Crawling;

/// <summary>
/// Отвечает за асинхронную загрузку HTML-страниц по сети.
/// Использует один общий HttpClient (так рекомендует Microsoft:
/// создавать по одному клиенту на всё приложение, а не на каждый запрос).
/// </summary>
public class PageDownloader
{
    private readonly HttpClient _http;

    public PageDownloader(HttpClient http) => _http = http;

    /// <summary>
    /// Асинхронно загрузить страницу.
    /// Возвращает кортеж: получилось ли, HTML-код, размер в байтах и текст ошибки (если была).
    /// async/await позволяет не блокировать поток, пока идёт ожидание ответа от сервера —
    /// именно за счёт этого мы получаем большой выигрыш при параллельной загрузке.
    /// </summary>
    public async Task<(bool Ok, string Html, int Bytes, string? Error)> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            // ResponseHeadersRead: не ждём загрузки всего тела сразу, экономим память.
            using HttpResponseMessage response =
                await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            response.EnsureSuccessStatusCode(); // бросит исключение, если код ответа не 2xx

            // Нас интересует только HTML. Картинки, PDF и прочее пропускаем.
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return (false, string.Empty, 0, $"пропущен не-HTML контент ({mediaType})");

            string html = await response.Content.ReadAsStringAsync(ct);
            int bytes = Encoding.UTF8.GetByteCount(html);
            return (true, html, bytes, null);
        }
        catch (OperationCanceledException)
        {
            // Отмену не глушим, а пробрасываем выше — это штатный сигнал "пора останавливаться".
            throw;
        }
        catch (Exception ex)
        {
            // Любую сетевую/прочую ошибку превращаем в "неуспех" — система продолжит работу.
            return (false, string.Empty, 0, ex.Message);
        }
    }
}
