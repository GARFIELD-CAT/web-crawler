namespace DistributedWebCrawler.Interfaces;

// Интерфейс логирования. Благодаря интерфейсу мы можем легко заменить вывод в консоль на запись в файл или куда-то ещё, не меняя остальной код.
public interface ILogger
{
    // Обычное информационное сообщение/
    void Info(string message);

    // Предупреждение (что-то пошло не идеально, но работа продолжается).
    void Warn(string message);

    // Ошибка. Можно передать исключение для подробностей.
    void Error(string message, Exception? exception = null);
}
