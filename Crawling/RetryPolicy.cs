namespace DistributedWebCrawler.Crawling;

// Политика повторных попыток (ретраев) при загрузке страницы.
// Пауза между попытками растёт экспоненциально (backoff): базовая, ×2, ×4 и т.д.,
// чтобы не «долбить» перегруженный сервер.
public class RetryPolicy
{
    // Максимальная пауза между попытками (страховка от слишком больших задержек).
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    private readonly HashSet<int> _retryableStatusCodes;

    // Сколько всего попыток (1 основная + повторы).
    public int MaxAttempts { get; }

    // Базовая пауза перед первым повтором.
    public TimeSpan BaseDelay { get; }

    public RetryPolicy(int retries, TimeSpan baseDelay, IEnumerable<int>? retryableStatusCodes = null)
    {
        MaxAttempts = Math.Max(1, retries + 1); // retries=2 -> 3 попытки всего
        BaseDelay = baseDelay;
        _retryableStatusCodes = retryableStatusCodes is null
            ? new HashSet<int> { 408, 425, 429, 500, 502, 503, 504 }
            : new HashSet<int>(retryableStatusCodes);
    }

    // Нужно ли повторять запрос при таком HTTP-статусе ответа.
    public bool IsRetryableStatus(int statusCode) => _retryableStatusCodes.Contains(statusCode);

    // Пауза перед следующей попыткой (attempt — номер только что неудавшейся попытки, с 1).
    public TimeSpan DelayFor(int attempt)
    {
        double ms = BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        return ms >= MaxDelay.TotalMilliseconds ? MaxDelay : TimeSpan.FromMilliseconds(ms);
    }

    // Список повторяемых статусов (для вывода в лог/отчёт).
    public string DescribeStatuses() => string.Join(", ", _retryableStatusCodes.OrderBy(c => c));
}