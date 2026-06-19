namespace DistributedWebCrawler.Models;

/// <summary>
/// Результат обработки одной веб-страницы рабочим узлом.
/// Воркер скачивает страницу, разбирает её и заполняет этот объект,
/// после чего отправляет его мастеру по сети.
/// </summary>
public class PageData
{
    /// <summary>Адрес обработанной страницы.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Глубина страницы (берётся из исходной задачи).</summary>
    public int Depth { get; set; }

    /// <summary>Заголовок страницы (содержимое тега &lt;title&gt;).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Список слов со страницы (для построения поискового индекса).</summary>
    public List<string> Words { get; set; } = new();

    /// <summary>Найденные на странице ссылки (для дальнейшего обхода).</summary>
    public List<string> Links { get; set; } = new();

    /// <summary>Сколько байт занял HTML страницы (для статистики).</summary>
    public int ByteCount { get; set; }

    /// <summary>Успешно ли обработана страница.</summary>
    public bool Success { get; set; }

    /// <summary>Текст ошибки, если обработка не удалась (иначе null).</summary>
    public string? Error { get; set; }
}
