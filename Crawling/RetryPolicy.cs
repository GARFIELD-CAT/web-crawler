namespace DistributedWebCrawler.Crawling;

/// <summary>
/// Политика повторных попыток (ретраев) при загрузке страницы.
///
/// Идея: некоторые ошибки временные — сервер перегружен (5xx), превышен лимит
/// запросов (429) или запрос не успел за таймаут. Такие запросы имеет смысл
/// повторить через паузу. А ошибки вроде 404 (страницы нет) повторять бесполезно —
/// результат не изменится.
///
/// Пауза между попытками растёт экспоненциально (backoff): базовая, ×2, ×4 и т.д.,
/// чтобы не «долбить» перегруженный сервер.
/// </summary>
public class RetryPolicy
{
    // Максимальная пауза между попытками (страховка от слишком больших задержек).
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    private readonly HashSet<int> _retryableStatusCodes;

    /// <summary>Сколько всего попыток (1 основная + повторы).</summary>
    public int MaxAttempts { get; }

    /// <summary>Базовая пауза перед первым повтором.</summary>
    public TimeSpan BaseDelay { get; }

    /// <param name="retries">Число ПОВТОРОВ (0 — повторов нет).</param>
    /// <param name="baseDelay">Базовая пауза, дальше удваивается.</param>
    /// <param name="retryableStatusCodes">
    /// Какие HTTP-статусы повторять. Если null — берётся набор по умолчанию
    /// (408, 425, 429, 500, 502, 503, 504).
    /// </param>
    public RetryPolicy(int retries, TimeSpan baseDelay, IEnumerable<int>? retryableStatusCodes = null)
    {
        MaxAttempts = Math.Max(1, retries + 1); // retries=2 -> 3 попытки всего
        BaseDelay = baseDelay;
        _retryableStatusCodes = retryableStatusCodes is null
            ? new HashSet<int> { 408, 425, 429, 500, 502, 503, 504 }
            : new HashSet<int>(retryableStatusCodes);
    }

    /// <summary>Нужно ли повторять запрос при таком HTTP-статусе ответа.</summary>
    public bool IsRetryableStatus(int statusCode) => _retryableStatusCodes.Contains(statusCode);

    /// <summary>Пауза перед следующей попыткой (attempt — номер только что неудавшейся попытки, с 1).</summary>
    public TimeSpan DelayFor(int attempt)
    {
        double ms = BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        return ms >= MaxDelay.TotalMilliseconds ? MaxDelay : TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>Список повторяемых статусов (для вывода в лог/отчёт).</summary>
    public string DescribeStatuses() => string.Join(", ", _retryableStatusCodes.OrderBy(c => c));
}