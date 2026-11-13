# Шпаргалка для защиты проекта (Вопросы и точные ответы по коду)

## 1. Как работает SignalR и чем он отличается от обычных HTTP-запросов?
- **Обычный HTTP (Request-Response):** Клиент каждый раз открывает запрос, отправляет заголовки, ждёт ответ и закрывает обмен. Чтобы узнавать о новых игроках или тиках таймера, пришлось бы делать *polling* (опрос каждые 500 мс), что создаёт лишнюю нагрузку и задержки.
- **SignalR:** Поверх ASP.NET Core устанавливает постоянное полнодуплексное соединение (предпочтительно **WebSockets**, с автоматическим откатом на Server-Sent Events или Long Polling). Сервер сам инициирует отправку событий (`QuestionStarted`, `TimerTicked`, `QuestionRevealed`) всем участникам группы комнаты `Clients.Group(roomCode)` за миллисекунды.
- В нашем проекте используется строго типизированный хаб `public sealed class GameHub : Hub<IGameHubClient>, IGameHubServer` (файл `ArenaQuiz/Hubs/GameHub.cs`), поэтому названия методов проверяются компилятором C#.

## 2. Где хранится состояние игры и что будет при нескольких одновременных комнатах?
- **Постоянные данные** (квизы, вопросы, учётные записи, завершённые сессии и результаты игроков) хранятся в **SQLite** через `ArenaDbContext` (`ArenaQuiz/Data/ArenaDbContext.cs`).
- **Оперативное состояние активных комнат** хранится в памяти сервера в потокобезопасном словаре `ConcurrentDictionary<string, ActiveRoomState> _rooms` внутри singleton-сервиса `GameRoomService` (`ArenaQuiz/Services/GameRoomService.cs`).
- **Изоляция комнат:** Каждая комната имеет уникальный 6-символьный PIN (`AQxxxx`), свой собственный объект синхронизации `SyncRoot`, свой словарь игроков, свои таймеры `System.Threading.Timer` и отдельную SignalR-группу `Groups.AddToGroupAsync(connectionId, roomCode)`. Десятки комнат могут идти одновременно, не блокируя друг друга и не смешивая события.

## 3. Как вы защищаетесь от читерства (почему таймер и очки считаются на сервере)?
Мы применяем два уровня защиты:
1. **Нулевое доверие к клиентскому времени и очкам:** Клиент отправляет только `SubmitAnswerRequest(RoomCode, PlayerId, QuestionIndex, SelectedOptionIndex)` — он **не** передаёт ни `elapsedMs`, ни баллы. При старте вопроса сервер сохраняет `room.QuestionStartedAtUtc = DateTime.UtcNow`, а при получении ответа вычисляет `elapsedMs = (DateTime.UtcNow - room.QuestionStartedAtUtc).TotalMilliseconds`. Повторные ответы и ответы после истечения окна (`TimeLimitSeconds + 1.5s`) отклоняются сервером.
2. **Сокрытие правильного ответа на уровне DTO:** Во время активного раунда клиентам отправляется проекция `LiveQuestionViewDto` (`ArenaQuiz.Shared/Models/GameLiveDtos.cs`), в которой **отсутствует** свойство `CorrectOptionIndex`. Даже если игрок откроет Chrome DevTools -> Network -> WS Frames, он не увидит правильный вариант до наступления фазы `QuestionReveal`.

## 4. Как масштабировать систему на 1,000+ одновременных игроков?
- **Текущая конфигурация** на одном экземпляре ASP.NET Core легко держит сотни одновременных WebSocket-подключений, так как расчёт очков занимает $O(1)$, а сортировка лидерборда $O(P \log P)$ выполняется только при завершении вопроса.
- **При масштабировании на несколько серверов (Horizontal Scaling):**
  1. Подключается **Redis Backplane** (`AddSignalR().AddStackExchangeRedis(...)`) или **Azure SignalR Service** для маршрутизации `Clients.Group(roomCode)` между разными узлами.
  2. Состояние активных комнат из `ConcurrentDictionary` переносится в **Redis** (распределённый кэш с атомарными Lua/Hash-операциями), а SQLite заменяется на **PostgreSQL** для конкурентной записи из множества инстансов.
  3. Статические файлы Blazor WebAssembly отдаются через **CDN** (сервер вообще не тратит CPU на рендеринг UI).

## 5. Как работают `EventCallback`, `CascadingValue` и `RenderFragment<T>` в вашем коде?
- **`EventCallback<T>`:** Используется для связи "снизу вверх" (от дочернего компонента к родительской странице). Например, в `QuestionCard.razor` объявлен `[Parameter] public EventCallback<int> OnOptionSelected { get; set; }`. Когда игрок нажимает вариант, компонент вызывает `await OnOptionSelected.InvokeAsync(index)`, а страница `PlayArena.razor` перехватывает его и отправляет ответ в SignalR. В `PlayerRoster.razor` аналогично работает `EventCallback<string> OnRemovePlayer`.
- **`CascadingValue`:** В `MainLayout.razor` всё содержимое `@Body` обёрнуто в `<CascadingValue Value="GameState" IsFixed="true">`. Любой вложенный компонент (например, `ConnectionStatusBanner.razor`) получает доступ к состоянию SignalR через `[CascadingParameter] public GameStateContainer? GameState { get; set; }` без передачи параметров вручную через каждый промежуточный слой.
- **Templated Component (`RenderFragment<TItem>`):** Реализован в `TemplatedMetricGrid.razor` (`@typeparam TItem`) и используется на странице `HistoryAnalytics.razor` для гибкого рендеринга истории игр с пользовательскими шаблонами `HeaderTemplate` и `RowTemplate`.

## 6. Какие приёмы функционального программирования применены?
- Иммутабельные `sealed record` типы для всех DTO и `with`-выражения.
- Чистые функции без побочных эффектов в `ScoreEngine` (`CalculateAnswerScore`, `ComputeLeaderboard`, `ComputeTeamStandings`, `BuildSessionCsv`).
- Функциональный тип `Result<T>` с комбинаторами `.Map()` и `.Match()` для обработки ошибок без бросания исключений в штатных сценариях.
- Декларативные LINQ-конвейеры и C# switch expressions.

