using System.Text;
using DistributedWebCrawler.Interfaces;

namespace DistributedWebCrawler.Crawling;

/// <summary>
/// Отвечает за асинхронную загрузку HTML-страниц по сети.
/// Использует один общий HttpClient (так рекомендует Microsoft:
/// создавать по одному клиенту на всё приложение, а не на каждый запрос).
///
/// Поддерживает повторные попытки (ретраи) по заданной политике: временные ошибки
/// (5xx, 429, таймаут, сетевой сбой) повторяются с паузой, а "постоянные" (например,
/// 404) — нет.
/// </summary>
public sealed class PageDownloader
{
    private readonly HttpClient _http;
    private readonly RetryPolicy _retryPolicy;
    private readonly ILogger _logger;

    public PageDownloader(HttpClient http, RetryPolicy retryPolicy, ILogger logger)
    {
        _http = http;
        _retryPolicy = retryPolicy;
        _logger = logger;
    }

    /// <summary>
    /// Асинхронно загрузить страницу с учётом политики ретраев.
    /// Возвращает кортеж: получилось ли, HTML-код, размер в байтах и текст ошибки (если была).
    /// </summary>
    public async Task<(bool Ok, string Html, int Bytes, string? Error)> DownloadAsync(string url, CancellationToken ct)
    {
        string? lastError = null;

        for (int attempt = 1; attempt <= _retryPolicy.MaxAttempts; attempt++)
        {
            bool retryable = false;
            try
            {
                // ResponseHeadersRead: не ждём загрузки всего тела сразу, экономим память.
                using HttpResponseMessage response =
                    await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

                if (response.IsSuccessStatusCode)
                {
                    // Нас интересует только HTML. Картинки, PDF и прочее пропускаем (это НЕ ошибка для ретрая).
                    string? mediaType = response.Content.Headers.ContentType?.MediaType;
                    if (mediaType is not null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                        return (false, string.Empty, 0, $"пропущен не-HTML контент ({mediaType})");

                    string html = await response.Content.ReadAsStringAsync(ct);
                    int bytes = Encoding.UTF8.GetByteCount(html);
                    return (true, html, bytes, null);
                }

                // Ответ не 2xx. Решаем по статусу, повторять ли.
                int statusCode = (int)response.StatusCode;
                lastError = $"HTTP {statusCode} ({response.ReasonPhrase})";

                if (!_retryPolicy.IsRetryableStatus(statusCode))
                    return (false, string.Empty, 0, lastError); // например, 404 — повторять нет смысла

                retryable = true; // например, 500/503 — попробуем ещё раз
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Настоящая отмена (Ctrl+C / команда Stop) — пробрасываем, это не ошибка для ретрая.
                throw;
            }
            catch (OperationCanceledException)
            {
                // ct НЕ отменён, но операция отменена => это таймаут HttpClient. Повторяемо.
                lastError = "таймаут запроса";
                retryable = true;
            }
            catch (HttpRequestException ex)
            {
                // Сетевой сбой (DNS, отказ в соединении и т.п.) — обычно временный, повторяем.
                lastError = $"сетевая ошибка: {ex.Message}";
                retryable = true;
            }
            catch (Exception ex)
            {
                // Прочие ошибки не повторяем — сразу возвращаем неуспех.
                return (false, string.Empty, 0, ex.Message);
            }

            // Сюда попадаем только при повторяемой ошибке. Если попытки ещё есть — ждём и повторяем.
            if (retryable && attempt < _retryPolicy.MaxAttempts)
            {
                TimeSpan delay = _retryPolicy.DelayFor(attempt);
                _logger.Warn($"Повтор {url} (попытка {attempt + 1}/{_retryPolicy.MaxAttempts}) через {delay.TotalMilliseconds:F0} мс: {lastError}");
                await Task.Delay(delay, ct);
            }
        }

        // Все попытки исчерпаны.
        return (false, string.Empty, 0, $"не удалось после {_retryPolicy.MaxAttempts} попыток: {lastError}");
    }
}