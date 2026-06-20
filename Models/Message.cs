namespace DistributedWebCrawler.Models;

// Тип сетевого сообщения. По нему получатель понимает, что делать с сообщением.
public enum MessageType
{
    // Воркер → Мастер: "я подключился, готов к работе".
    Register,

    // Воркер → Мастер: "пульс" — я ещё жив (отправляется периодически).
    Heartbeat,

    // Воркер → Мастер: результат обработки страницы.
    Result,

    // Мастер → Воркер: вот тебе задача (URL для обхода).
    Assign,

    // Мастер → Воркер: останавливай работу, обход завершён.
    Stop
}

// Универсальный "конверт" для всех сообщений между мастером и воркерами.
public class Message
{
    // Тип сообщения (см. MessageType).
    public MessageType Type { get; set; }

    // Идентификатор воркера (кто отправил/кому адресовано).
    public string WorkerId { get; set; } = string.Empty;

    // Задача — заполняется в сообщении Assign.
    public CrawlTask? Task { get; set; }

    // Результат — заполняется в сообщении Result.
    public PageData? Result { get; set; }

    // Максимальный параллелизм воркера — заполняется в Register.
    public int MaxParallelism { get; set; }

    // Сколько страниц воркер уже обработал — заполняется в Heartbeat.
    public long Processed { get; set; }
}
