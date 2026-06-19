# Руководство по реализации

Документ объясняет ключевые компоненты изнутри, показывает примеры использования
основных классов и даёт рекомендации по запуску и настройке.

---

## 1. Ключевые компоненты

### 1.1. `PageDownloader` — загрузка страниц

Асинхронно скачивает HTML по одному общему `HttpClient`. Возвращает кортеж
«получилось / HTML / размер / ошибка» и **никогда не роняет программу** на сетевой
ошибке (превращает её в «неуспех»). Отмену (`OperationCanceledException`)
пробрасывает наверх — это штатный сигнал остановки.

```csharp
var http = new HttpClient();
var downloader = new PageDownloader(http);

(bool ok, string html, int bytes, string? error) =
    await downloader.DownloadAsync("https://books.toscrape.com/", CancellationToken.None);

if (ok)
    Console.WriteLine($"Скачано {bytes} байт");
```

### 1.2. `HtmlParser` — разбор HTML

Извлекает заголовок, слова и ссылки регулярными выражениями. Относительные ссылки
превращаются в абсолютные относительно базового адреса.

```csharp
IHtmlParser parser = new HtmlParser();
string title = parser.ExtractTitle(html);
IReadOnlyList<string> words = parser.ExtractWords(html);
IReadOnlyList<string> links = parser.ExtractLinks(html, "https://books.toscrape.com/");
```

### 1.3. `CrawlPipeline` — конвейер на TPL Dataflow

Сердце параллельной обработки. Три «станции»: загрузка → разбор → выдача. Задачи
добавляются по одной (`Post`), а в конце вызывается `CompleteAsync()`.

```csharp
var pipeline = new CrawlPipeline(
    downloader, parser,
    maxParallelism: 8,
    onResult: page => Console.WriteLine($"Готово: {page.Url}"),
    ct: CancellationToken.None);

pipeline.Post(new CrawlTask("https://books.toscrape.com/", 0));
await pipeline.CompleteAsync(); // дождаться обработки всех задач
```

Параметр `maxParallelism` определяет, сколько страниц обрабатывается одновременно.

### 1.4. `InvertedIndex` — индекс и поиск

Хранит соответствие «слово → страницы, где оно встречается». Поиск складывает
релевантность по словам запроса и ранжирует результаты через PLINQ.

```csharp
var index = new InvertedIndex();
index.AddDocument(pageData);

IReadOnlyList<SearchResult> results = index.Search("history england", maxResults: 10);
foreach (var r in results)
    Console.WriteLine($"[{r.Score}] {r.Title} — {r.Url}");
```

### 1.5. `IDistributionStrategy` — выбор воркера (паттерн Strategy)

```csharp
IDistributionStrategy strategy = new LeastLoadedStrategy();
WorkerInfo? chosen = strategy.SelectWorker(aliveWorkers);
```

- `RoundRobinStrategy` — раздаёт по очереди.
- `LeastLoadedStrategy` — отдаёт наименее загруженному воркеру.

### 1.6. `MasterServer` и `WorkerClient` — сетевая координация

```csharp
// Мастер
var master = new MasterServer(
    port: 5000, logger, new RoundRobinStrategy(),
    maxDepth: 2, maxPages: 50, politenessDelayMs: 200);
await master.RunAsync("https://books.toscrape.com/", ct);

// Воркер
var worker = new WorkerClient(
    "localhost", 5000, "worker-1",
    maxParallelism: 8, logger, downloader, parser);
await worker.RunAsync(ct);
```

---

## 2. Как компоненты взаимодействуют

1. `Program` разбирает аргументы и создаёт нужный объект (`MasterServer`,
   `WorkerClient` или `BenchmarkRunner`).
2. `MasterServer` использует `IDistributionStrategy`, `InvertedIndex`,
   `Statistics`, `SystemMonitor` и `MessageProtocol`.
3. `WorkerClient` использует `CrawlPipeline` (а тот — `PageDownloader` и
   `HtmlParser`) и `MessageProtocol`.
4. Мастер и воркер обмениваются объектами `Message` через `MessageProtocol`
   поверх TCP.

Слабая связанность достигается за счёт интерфейсов и передачи зависимостей через
конструкторы (вместо обращения к глобальным переменным).

---

## 3. Управление параллелизмом и потокобезопасностью

| Что | Чем обеспечивается |
|-----|--------------------|
| Параллельная загрузка/разбор | `MaxDegreeOfParallelism` в блоках Dataflow |
| Очередь задач | `BlockingCollection` (безопасна для многих потоков) |
| Общие словари (индекс, посещённые) | `ConcurrentDictionary` |
| Счётчики | `Interlocked` (атомарные операции) |
| Запись в один сетевой поток | `SemaphoreSlim` (по одному «писателю») |
| Аккуратный вывод в консоль | `lock` (единственное место) |

Важная деталь: цикл раздачи задач в мастере использует
`BlockingCollection.GetConsumingEnumerable`, который **блокирует поток** в ожидании
новых задач. Это допустимо, потому что таких циклов один и он работает в отдельной
фоновой задаче. В высоконагруженных системах для этого чаще берут
`System.Threading.Channels`, но для учебного примера `BlockingCollection`
нагляднее и прямо рекомендован заданием.

---

## 4. Обработка ошибок

- **Сетевые ошибки загрузки** — превращаются в «неуспех», обход продолжается.
- **Обрыв соединения с воркером** — мастер ловит исключение, удаляет воркера и
  переназначает его задачи.
- **Некорректные данные в сети** — `MessageProtocol` проверяет длину сообщения и
  бросает понятное исключение.
- **Отмена** — `OperationCanceledException` везде обрабатывается как штатное
  завершение, а не как ошибка.

---

## 5. Рекомендации по запуску и настройке

- **Сначала мастер, потом воркеры.** Мастер должен слушать порт до подключения
  воркеров (хотя воркеры умеют переподключаться повторным запуском).
- **Несколько воркеров — несколько терминалов.** Задавайте им разные `--id`.
- **Скорость против вежливости.** Параметр `--delay` регулирует паузу между
  выдачами задач. Меньше задержка — быстрее обход, но выше нагрузка на сайт.
- **Глубина и лимит.** `--depth` и `--pages` ограничивают объём обхода — удобно
  для демонстрации, чтобы система завершалась за разумное время.
- **Стратегия.** Для демонстрации балансировки используйте
  `--strategy leastloaded` и воркеры с разным `--parallelism`.
- **Свой сайт.** Меняйте `--seed`, но обходите только разрешённые ресурсы.

---

## 6. Как продемонстрировать ключевые возможности

| Что показать | Как |
|--------------|-----|
| Распределённую работу | мастер + 2–3 воркера в разных терминалах |
| Балансировку | `--strategy leastloaded`, воркеры с разным `--parallelism` |
| Отказоустойчивость | закрыть один воркер во время обхода |
| Мониторинг | смотреть строки «МОНИТОР» в терминале мастера |
| Поиск (PLINQ) | ввести запрос в приглашении `поиск>` после обхода |
| Ускорение | запустить `benchmark` и сравнить времена |
