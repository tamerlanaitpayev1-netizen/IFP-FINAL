# Командный Git-воркфлоу для защиты (Тамерлан + Диас)

По требованиям курса **оба участника обязаны иметь реальную историю коммитов в GitHub-репозитории**. Ниже приведена чистая схема разделения веток и коммитов, а также инструкция по сборке ZIP-архива без `bin/`, `obj/` и `.vs/`.

## 1. Инициализация репозитория
```bash
git init
git branch -M main
git add .gitignore README.md ArenaQuiz.sln ArenaQuiz.Shared/
git commit -m "chore: initialize solution structure and shared contracts"
```

## 2. Ветка Студента A (Тамерлан — Server, SignalR, SQLite, xUnit)
```bash
git checkout -b feat/server-engine
git add ArenaQuiz/Data/ ArenaQuiz/Services/ ArenaQuiz/Hubs/ ArenaQuiz/Controllers/ ArenaQuiz/Program.cs
git commit -m "feat(server): implement GameRoomService, SignalR GameHub, SQLite EF Core & RBAC"
git add ArenaQuiz.Tests/ScoreEngineTests.cs ArenaQuiz.Tests/GameRoomServiceTests.cs ArenaQuiz.Tests/QuizRepositorySqliteTests.cs
git commit -m "test(server): add xUnit unit and SQLite integration tests"
git checkout main
git merge --no-ff feat/server-engine
```

## 3. Ветка Студента B (Диас — Blazor WASM, Components, UI/UX, bUnit)
```bash
git checkout -b feat/blazor-client
git add ArenaQuiz.Client/ ArenaQuiz/Components/ ArenaQuiz/wwwroot/
git commit -m "feat(client): build responsive Blazor WASM pages, reusable components & WebAudio"
git add ArenaQuiz.Tests/BlazorComponentsBunitTests.cs
git commit -m "test(client): add bUnit tests for QuestionCard, RoundTimer, Leaderboard, Roster & TemplatedGrid"
git checkout main
git merge --no-ff feat/blazor-client
```

## 4. Документация и презентация
```bash
git add docs/ README.md
git commit -m "docs: add Mermaid architecture diagrams, presentation slides and defense Q&A"
git push -u origin main
```

## 5. Создание чистого ZIP-архива (без bin, obj, .vs, .db)
В PowerShell из корня проекта:
```powershell
git archive --format=zip --output=ArenaQuiz-submission.zip HEAD
```
`git archive` автоматически исключает всё, что перечислено в `.gitignore` (`bin/`, `obj/`, `.vs/`, `*.db`).

