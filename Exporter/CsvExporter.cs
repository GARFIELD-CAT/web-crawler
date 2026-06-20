using System.Globalization;
using System.Text;
using DistributedWebCrawler.Interfaces;
using DistributedWebCrawler.Models;

namespace DistributedWebCrawler.Export;

// Выгружает собранные данные (объекты PageData) в CSV-файл.
public class CsvExporter
{
    private readonly ILogger _logger;

    public CsvExporter(ILogger logger) => _logger = logger;


    // Записать страницы в CSV-файл.
    public void Export(string path, IEnumerable<PageData> pages, int maxWords = 100)
    {
        // UTF8Encoding(true) добавляет BOM — без него Excel может показать кириллицу
        // как "кракозябры".
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        // Строка-заголовок с именами столбцов.
        writer.WriteLine("Url,Depth,Title,Words,ByteCount,Success");

        int rows = 0;
        foreach (PageData page in pages)
        {
            // Берём первые maxWords слов и склеиваем их через пробел в одну ячейку.
            string words = string.Join(' ', page.Words.Take(maxWords));

            // Собираем строку из экранированных значений.
            string line = string.Join(',',
                Escape(page.Url),
                page.Depth.ToString(CultureInfo.InvariantCulture),
                Escape(page.Title),
                Escape(words),
                page.ByteCount.ToString(CultureInfo.InvariantCulture),
                page.Success ? "true" : "false");

            writer.WriteLine(line);
            rows++;
        }

        _logger.Info($"Данные выгружены в CSV: {Path.GetFullPath(path)} (строк: {rows})");
    }

    // Экранировать одно значение по правилам CSV.
    private static string Escape(string value)
    {
        // Если есть "опасные" символы — оборачиваем в кавычки и удваиваем кавычки внутри.
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";

        return value;
    }
}