using System.Collections.Concurrent;
using DistributedWebCrawler.Models;

namespace DistributedWebCrawler.Indexing;

/// <summary>Одна строка результата поиска.</summary>
public record SearchResult(string Url, string Title, int Score);

/// <summary>
/// Инвертированный индекс — структура, как в поисковиках.
/// Обычный текст: "страница -> её слова".
/// Инвертированный индекс: "слово -> на каких страницах оно встречается (и сколько раз)".
/// Так можно мгновенно находить страницы по слову.
///
/// Используем ConcurrentDictionary, потому что результаты добавляются из разных
/// потоков одновременно (мастер получает их от нескольких воркеров).
/// </summary>
public class InvertedIndex
{
    // слово -> (URL -> сколько раз это слово встретилось на этой странице)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _index = new();

    // URL -> заголовок страницы (чтобы красиво показывать результаты поиска)
    private readonly ConcurrentDictionary<string, string> _titles = new();

    // Счётчик проиндексированных документов (меняем атомарно).
    private long _documentCount;

    /// <summary>Сколько страниц проиндексировано.</summary>
    public long DocumentCount => Interlocked.Read(ref _documentCount);

    /// <summary>Сколько различных слов (терминов) в индексе.</summary>
    public int TermCount => _index.Count;

    /// <summary>Добавить страницу в индекс.</summary>
    public void AddDocument(PageData page)
    {
        if (!page.Success)
            return;

        _titles[page.Url] = page.Title;
        Interlocked.Increment(ref _documentCount);

        foreach (string word in page.Words)
        {
            // GetOrAdd: если слова ещё нет — создаём для него вложенный словарь.
            ConcurrentDictionary<string, int> postings =
                _index.GetOrAdd(word, _ => new ConcurrentDictionary<string, int>());

            // AddOrUpdate: если URL ещё не встречался для этого слова — ставим 1,
            // иначе увеличиваем счётчик на 1. Всё это потокобезопасно.
            postings.AddOrUpdate(page.Url, 1, (_, count) => count + 1);
        }
    }

    /// <summary>
    /// Поиск страниц по запросу из нескольких слов.
    /// Релевантность (Score) — сумма того, сколько раз слова запроса встретились на странице.
    /// Финальное ранжирование делаем через PLINQ (параллельный LINQ).
    /// </summary>
    public IReadOnlyList<SearchResult> Search(string query, int maxResults = 10)
    {
        // Разбиваем запрос на слова (по пробелам).
        string[] terms = query.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (terms.Length == 0)
            return Array.Empty<SearchResult>();

        // Собираем "очки" по каждой подходящей странице.
        var scores = new ConcurrentDictionary<string, int>();
        foreach (string term in terms)
        {
            if (_index.TryGetValue(term, out ConcurrentDictionary<string, int>? postings))
            {
                foreach (KeyValuePair<string, int> entry in postings)
                    scores.AddOrUpdate(entry.Key, entry.Value, (_, sum) => sum + entry.Value);
            }
        }

        // PLINQ: .AsParallel() заставляет LINQ-запрос выполняться на нескольких ядрах.
        // Здесь мы параллельно превращаем пары "URL -> очки" в результаты,
        // сортируем по убыванию релевантности и берём верхние maxResults.
        return scores
            .AsParallel()
            .Select(entry => new SearchResult(
                entry.Key,
                _titles.TryGetValue(entry.Key, out string? title) ? title : string.Empty,
                entry.Value))
            .OrderByDescending(result => result.Score)
            .Take(maxResults)
            .ToArray();
    }
}
