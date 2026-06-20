using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DistributedWebCrawler.Models;

namespace DistributedWebCrawler.Indexing;

// Одна строка результата поиска.
public record SearchResult(string Url, string Title, int Score);

// <summary>
// Инвертированный ПОЗИЦИОННЫЙ индекс — структура, как в поисковиках.
// Обычный текст: "страница -> её слова".
// Инвертированный индекс: "слово -> на каких страницах оно встречается".
//
// Здесь для каждого слова и страницы хранится не просто счётчик, а СПИСОК ПОЗИЦИЙ
// (на каком по счёту месте в тексте слово стоит). Позиции нужны для поиска по
// точной фразе: фраза найдена, если её слова идут на странице подряд
// (позиции p, p+1, p+2, ...). Число вхождений слова = длина списка позиций.
//
// Используем ConcurrentDictionary, потому что страницы добавляются из разных
// потоков одновременно (мастер получает результаты от нескольких воркеров).
// Список позиций для конкретной пары (слово, URL) заполняется только одним потоком
public sealed class InvertedIndex
{
    // слово -> (URL -> список позиций этого слова на странице)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, List<int>>> _index = new();

    // URL -> заголовок страницы (для красивого вывода результатов)
    private readonly ConcurrentDictionary<string, string> _titles = new();

    // Счётчик проиндексированных документов (меняем атомарно).
    private long _documentCount;

    // Та же логика выделения "слов", что и в HtmlParser
    private static readonly Regex WordRegex = new(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);

    // Сколько страниц проиндексировано.
    public long DocumentCount => Interlocked.Read(ref _documentCount);

    // Сколько различных слов (терминов) в индексе.
    public int TermCount => _index.Count;

    // Добавить страницу в индекс.
    public void AddDocument(PageData page)
    {
        if (!page.Success)
            return;

        _titles[page.Url] = page.Title;
        Interlocked.Increment(ref _documentCount);

        // Идём по словам по порядку: индекс pos и есть позиция слова в тексте.
        for (int pos = 0; pos < page.Words.Count; pos++)
        {
            string word = page.Words[pos];

            // GetOrAdd: если слова ещё нет — создаём для него вложенный словарь.
            ConcurrentDictionary<string, List<int>> postings =
                _index.GetOrAdd(word, _ => new ConcurrentDictionary<string, List<int>>());

            // Список позиций этого слова на данной странице.
            List<int> positions = postings.GetOrAdd(page.Url, _ => new List<int>());
            positions.Add(pos);
        }
    }

    // Обычный поиск: страницы, где встречается ХОТЯ БЫ ОДНО слово запроса.
    // Релевантность (Score) — сумма числа вхождений слов запроса на странице.
    public IReadOnlyList<SearchResult> Search(string query, int maxResults = 10)
    {
        string[] terms = Tokenize(query);
        if (terms.Length == 0)
            return Array.Empty<SearchResult>();

        var scores = new ConcurrentDictionary<string, int>();
        foreach (string term in terms)
        {
            if (_index.TryGetValue(term, out ConcurrentDictionary<string, List<int>>? postings))
            {
                foreach (KeyValuePair<string, List<int>> entry in postings)
                {
                    int count = entry.Value.Count; // число вхождений = длина списка позиций
                    scores.AddOrUpdate(entry.Key, count, (_, sum) => sum + count);
                }
            }
        }

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

    // Поиск по точной фразе: страница подходит, только если слова запроса встречаются
    // Score — сколько раз фраза встретилась на странице.
    public IReadOnlyList<SearchResult> SearchPhrase(string phrase, int maxResults = 10)
    {
        string[] terms = Tokenize(phrase);
        if (terms.Length == 0)
            return Array.Empty<SearchResult>();

        // Одно слово — это обычный поиск.
        if (terms.Length == 1)
            return Search(terms[0], maxResults);

        // Берём постинги (URL -> позиции) для каждого слова фразы.
        // Если хотя бы одного слова нет в индексе — такой фразы нет нигде.
        var perTerm = new ConcurrentDictionary<string, List<int>>[terms.Length];
        for (int i = 0; i < terms.Length; i++)
        {
            if (!_index.TryGetValue(terms[i], out ConcurrentDictionary<string, List<int>>? postings))
                return Array.Empty<SearchResult>();
            perTerm[i] = postings;
        }

        // Кандидаты — страницы, где встречается первое слово. На каждой проверяем,
        // идут ли остальные слова сразу следом. Проверку кандидатов распараллеливаем (PLINQ).
        return perTerm[0]
            .AsParallel()
            .Select(entry => new SearchResult(
                entry.Key,
                _titles.TryGetValue(entry.Key, out string? title) ? title : string.Empty,
                CountPhraseOccurrences(perTerm, entry.Key)))
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .Take(maxResults)
            .ToArray();
    }

    // Сколько раз фраза встречается на странице url.
    // Для каждой позиции первого слова проверяем, что второе стоит на +1, третье на +2 и т.д.
    private static int CountPhraseOccurrences(ConcurrentDictionary<string, List<int>>[] perTerm, string url)
    {
        // Позиции первого слова на этой странице.
        if (!perTerm[0].TryGetValue(url, out List<int>? firstPositions))
            return 0;

        // Позиции остальных слов — как множества, чтобы быстро проверять наличие нужной позиции.
        var sets = new HashSet<int>[perTerm.Length];
        for (int i = 1; i < perTerm.Length; i++)
        {
            if (!perTerm[i].TryGetValue(url, out List<int>? positions))
                return 0; // слова нет на этой странице — фразы быть не может
            sets[i] = new HashSet<int>(positions);
        }

        int count = 0;
        foreach (int start in firstPositions)
        {
            bool isPhrase = true;
            for (int i = 1; i < perTerm.Length; i++)
            {
                if (!sets[i].Contains(start + i)) // i-е слово должно стоять на позиции start+i
                {
                    isPhrase = false;
                    break;
                }
            }
            if (isPhrase)
                count++;
        }
        return count;
    }

    // Разбить строку на слова ТОЧНО так же, как индексируется текст страниц:
    // буквы/цифры, нижний регистр, токены короче 3 символов отбрасываются.
    // Это важно для поиска по фразе — позиции слов запроса должны совпадать с индексом.
    private static string[] Tokenize(string text)
    {
        var words = new List<string>();
        foreach (Match match in WordRegex.Matches(text.ToLowerInvariant()))
        {
            if (match.Value.Length >= 3)
                words.Add(match.Value);
        }
        return words.ToArray();
    }
}