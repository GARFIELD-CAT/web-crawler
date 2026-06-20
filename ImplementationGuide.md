# Руководство по реализации

Документ объясняет ключевые компоненты изнутри, показывает примеры использования
классов и дает рекомендации по запуску и демонстрации.

---

## 1. Ключевые компоненты

### 1.1. `PageDownloader` — загрузка страниц с ретраями

Асинхронно скачивает HTML по общему `HttpClient`. Внутри — цикл повторных попыток по
`RetryPolicy`: временные ошибки (5xx, 429, таймаут, сетевой сбой) повторяются с
нарастающей паузой, а постоянные (например, 404) — нет. Отмену (`Ctrl+C`/`Stop`)
пробрасывает наверх.

```csharp
var policy = new RetryPolicy(retries: 2, baseDelay: TimeSpan.FromMilliseconds(500));
var downloader = new PageDownloader(httpClient, policy, logger);

(bool ok, string html, int bytes, string? error) =
    await downloader.DownloadAsync("https://books.toscrape.com/", CancellationToken.None);
```

### 1.2. `RetryPolicy` — какие ошибки и сколько раз повторять

Хранит число попыток, базовую паузу (растет ×2, потолок 30 с) и набор повторяемых
HTTP-статусов (по умолчанию `408, 425, 429, 500, 502, 503, 504`).

```csharp
// повторять только 500 и 503, 3 раза, базовая пауза 1 с
var policy = new RetryPolicy(3, TimeSpan.FromSeconds(1), new[] { 500, 503 });
bool retry = policy.IsRetryableStatus(503); // true
```

### 1.3. `HtmlParser` — разбор HTML

Извлекает заголовок, слова и ссылки регулярными выражениями. Относительные ссылки
превращаются в абсолютные относительно базового адреса; берутся только `http(s)`
ссылки из тегов `<a href>`, якоря (`#...`) и query/fragment отбрасываются.

```csharp
IHtmlParser parser = new HtmlParser();
string title = parser.ExtractTitle(html);
IReadOnlyList<string> words = parser.ExtractWords(html);
IReadOnlyList<string> links = parser.ExtractLinks(html, "https://books.toscrape.com/");
```

### 1.4. `CrawlPipeline` — конвейер на TPL Dataflow

Три блока: загрузка → разбор → выдача. Задачи добавляются по одной (`Post`), в
конце вызывается `CompleteAsync()`.

```csharp
var pipeline = new CrawlPipeline(downloader, parser, maxParallelism: 8,
    onResult: page => Console.WriteLine($"Готово: {page.Url}"),
    ct: CancellationToken.None);

pipeline.Post(new CrawlTask("https://books.toscrape.com/", 0));
await pipeline.CompleteAsync();
```

### 1.5. `InvertedIndex` — индекс и поиск (PLINQ)

Хранит «слово → страницы». Поиск суммирует релевантность по словам запроса и
ранжирует результаты через PLINQ (`AsParallel`).

```csharp
var index = new InvertedIndex();
index.AddDocument(pageData);
IReadOnlyList<SearchResult> results = index.Search("history england", maxResults: 10);
```

### 1.6. `IDistributionStrategy` — выбор воркера (Strategy)

```csharp
IDistributionStrategy strategy = new LeastLoadedStrategy();
WorkerInfo? chosen = strategy.SelectWorker(aliveWorkers);
```

`RoundRobinStrategy` — по очереди; `LeastLoadedStrategy` — наименее загруженному.

### 1.7. `MasterServer` и `WorkerClient` — координация

```csharp
// Мастер (последний параметр — путь к CSV; пустая строка = авто-имя)
var master = new MasterServer(port: 5000, logger, new RoundRobinStrategy(),
    maxDepth: 2, maxPages: 50, politenessDelayMs: 200, outputPath: "");
await master.RunAsync("https://books.toscrape.com/", ct);

// Воркер
var worker = new WorkerClient("localhost", 5000, "worker-1",
    maxParallelism: 8, logger, downloader, parser);
await worker.RunAsync(ct);
```

### 1.8. `CsvExporter` — выгрузка в CSV

Пишет успешные страницы с экранированием по RFC 4180 и BOM (для Excel). Столбцы:
`Url, Depth, Title, Words` (первые 100 слов), `ByteCount, Success`.

```csharp
var exporter = new CsvExporter(logger);
exporter.Export("results.csv", pages, maxWords: 100);
```

---

## 2. Как компоненты взаимодействуют

1. `Program` разбирает аргументы и создает нужный объект (`MasterServer`,
   `WorkerClient` или `BenchmarkRunner`), а также `RetryPolicy` для загрузчика.
2. `MasterServer` использует `IDistributionStrategy`, `InvertedIndex`, `Statistics`,
   `SystemMonitor`, `CsvExporter` и `MessageProtocol`.
3. `WorkerClient` использует `CrawlPipeline` (а тот — `PageDownloader` с `RetryPolicy`
   и `HtmlParser`) и `MessageProtocol`.
4. Мастер и воркер обмениваются объектами `Message` через `MessageProtocol` поверх TCP.

Слабая связанность достигается интерфейсами и передачей зависимостей через
конструкторы (без глобальных переменных).

---

## 3. Управление параллелизмом и потокобезопасностью

| Что                                              | Чем обеспечивается                         |
| ------------------------------------------------ | ------------------------------------------ |
| Параллельная загрузка/разбор                     | `MaxDegreeOfParallelism` в блоках Dataflow |
| Очередь задач                                    | `BlockingCollection`                       |
| Общие словари (индекс, посещенные, обработанные) | `ConcurrentDictionary`                     |
| Накопление результатов для CSV, примеры 404      | `ConcurrentQueue`                          |
| Счетчики                                         | `Interlocked`                              |
| Запись в один сетевой поток                      | `SemaphoreSlim`                            |
| Аккуратный вывод в консоль                       | `lock` (единственное место)                |

Деталь: цикл раздачи в мастере использует `BlockingCollection.GetConsumingEnumerable`,
который **блокирует** поток в ожидании задач. Это допустимо: такой цикл один и работает
в отдельной фоновой задаче. В высоконагруженных системах для этого чаще берут
`System.Threading.Channels`, но `BlockingCollection` нагляднее и рекомендован заданием.

---

## 4. Обработка ошибок и отказов

- **Временные ошибки загрузки** (5xx, 429, таймаут, сеть) — повторяются по
  `RetryPolicy`; если попытки исчерпаны, страница помечается неуспехом.
- **404 и прочие 4xx** — повторять бессмысленно, сразу неуспех; 404 не засоряют лог
  (считаются отдельно, в итогах выводится их число и до 10 примеров).
- **Обрыв соединения с воркером / отсутствие heartbeat** — мастер удаляет воркера и
  переназначает его задачи.
- **Повторный результат по одному URL** — игнорируется (`_completed`), чтобы не было
  дублей в индексе и CSV и преждевременного завершения обхода.
- **Некорректные данные в сети** — `MessageProtocol` проверяет длину сообщения.
- **Отмена** — `OperationCanceledException` обрабатывается как штатное завершение.
- **ASCII в заголовках** — значение `User-Agent` только из ASCII (иначе `HttpClient`
  отклонит запрос).

---

## 5. Рекомендации по запуску и настройке

- **Сначала мастер, потом воркеры.** Воркерам задавайте разные `--id`.
- **Скорость против вежливости.** `--delay` регулирует паузу между выдачами задач
  (действует глобально). Меньше задержка — быстрее обход, но выше нагрузка на сайт.
- **Глубина и лимит.** `--depth` и `--pages` ограничивают объем обхода.
- **Стратегия.** Для демонстрации балансировки: `--strategy leastloaded` и воркеры с
  разным `--parallelism`.
- **Ретраи.** `--retries`, `--retry-delay`, `--retry-status` настраивают повторы.
  Например: `--retries 3 --retry-delay 1000 --retry-status 500,503`.
- **CSV.** По умолчанию имя `хост_дата-время.csv`; свое — через `--output`.

---

## 6. Как продемонстрировать ключевые возможности

| Что показать          | Как                                                                        |
| --------------------- | -------------------------------------------------------------------------- |
| Распределенную работу | мастер + 2–3 воркера в разных терминалах                                   |
| Балансировку          | `--strategy leastloaded`, разный `--parallelism`                           |
| Отказоустойчивость    | закрыть один воркер во время обхода                                        |
| Ретраи                | временно «сломать» сеть/сайт или задать `--retry-status` под нужный статус |
| Мониторинг            | строки «МОНИТОР» в терминале мастера                                       |
| Поиск (PLINQ)         | ввести запрос в приглашении `поиск>` после обхода                          |
| Ускорение             | запустить `benchmark` и сравнить времена                                   |
| Выгрузку данных       | открыть полученный CSV-файл                                                |
