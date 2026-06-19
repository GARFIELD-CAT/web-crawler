namespace DistributedWebCrawler.Interfaces;

/// <summary>
/// Интерфейс логирования. Благодаря интерфейсу мы можем легко заменить вывод
/// в консоль на запись в файл или куда-то ещё, не меняя остальной код.
/// </summary>
public interface ILogger
{
    /// <summary>Обычное информационное сообщение.</summary>
    void Info(string message);

    /// <summary>Предупреждение (что-то пошло не идеально, но работа продолжается).</summary>
    void Warn(string message);

    /// <summary>Ошибка. Можно передать исключение для подробностей.</summary>
    void Error(string message, Exception? exception = null);
}
