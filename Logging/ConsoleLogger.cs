using DistributedWebCrawler.Interfaces;

namespace DistributedWebCrawler.Logging;

/// <summary>
/// Простой логгер, который пишет сообщения в консоль с цветом и временем.
/// </summary>
public class ConsoleLogger : ILogger
{
    // Объект-замок, чтобы сообщения из разных потоков не "перемешивались" на экране.
    // Console сам по себе потокобезопасен для одной операции, но смена цвета + вывод —
    // это две операции, поэтому защищаем их вместе. Это единственное оправданное
    // использование lock в проекте (для аккуратного вывода, а не для координации задач).
    private readonly object _sync = new();

    // Префикс показывает, кто пишет в лог: MASTER, WORKER-1 и т.п.
    private readonly string _prefix;

    public ConsoleLogger(string prefix) => _prefix = prefix;

    private void Write(string level, ConsoleColor color, string message)
    {
        lock (_sync)
        {
            ConsoleColor previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{_prefix}] [{level}] {message}");
            Console.ForegroundColor = previous;
        }
    }

    public void Info(string message) => Write("INFO", ConsoleColor.Gray, message);

    public void Warn(string message) => Write("WARN", ConsoleColor.Yellow, message);

    public void Error(string message, Exception? exception = null)
        => Write("ERROR", ConsoleColor.Red, exception is null ? message : $"{message} :: {exception.Message}");
}
