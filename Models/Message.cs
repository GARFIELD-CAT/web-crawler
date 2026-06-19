namespace DistributedWebCrawler.Models;

/// <summary>
/// Тип сетевого сообщения. По нему получатель понимает, что делать с сообщением.
/// </summary>
public enum MessageType
{
    /// <summary>Воркер → Мастер: "я подключился, готов к работе".</summary>
    Register,

    /// <summary>Воркер → Мастер: "пульс" — я ещё жив (отправляется периодически).</summary>
    Heartbeat,

    /// <summary>Воркер → Мастер: результат обработки страницы.</summary>
    Result,

    /// <summary>Мастер → Воркер: вот тебе задача (URL для обхода).</summary>
    Assign,

    /// <summary>Мастер → Воркер: останавливай работу, обход завершён.</summary>
    Stop
}

/// <summary>
/// Универсальный "конверт" для всех сообщений между мастером и воркерами.
/// Мы используем ОДИН класс с полем Type вместо множества разных классов —
/// так проще сериализовать в JSON и объяснять (для учебного проекта это удобнее).
/// Заполняются только те поля, которые нужны для конкретного типа сообщения.
/// </summary>
public class Message
{
    /// <summary>Тип сообщения (см. MessageType).</summary>
    public MessageType Type { get; set; }

    /// <summary>Идентификатор воркера (кто отправил/кому адресовано).</summary>
    public string WorkerId { get; set; } = string.Empty;

    /// <summary>Задача — заполняется в сообщении Assign.</summary>
    public CrawlTask? Task { get; set; }

    /// <summary>Результат — заполняется в сообщении Result.</summary>
    public PageData? Result { get; set; }

    /// <summary>Максимальный параллелизм воркера — заполняется в Register.</summary>
    public int MaxParallelism { get; set; }

    /// <summary>Сколько страниц воркер уже обработал — заполняется в Heartbeat.</summary>
    public long Processed { get; set; }
}
