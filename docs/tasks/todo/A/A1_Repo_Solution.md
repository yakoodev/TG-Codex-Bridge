# A1 — Solution/Projects: каркас репозитория (C#) + слои

## Цель
Собрать минимальный, но нормальный скелет решения: бот, инфраструктура, доступ к БД, запуск процессов.

## Сделать
- Создать `.sln` и проекты:
  - `TgCodexBridge.Bot` (консоль/worker)
  - `TgCodexBridge.Core` (доменные модели, интерфейсы)
  - `TgCodexBridge.Infrastructure` (SQLite, файловая система, process runner)
  - `TgCodexBridge.Tests` (минимальные интеграционные/юнит тесты)
- В `Core` определить интерфейсы:
  - `ITelegramClient` (абстракция над Telegram.Bot)
  - `IStateStore` (репозитории/DAO)
  - `ICodexRunner` (job runner)
  - `ITopicTitleFormatter`
  - `IPathPolicy` (проверки путей; по умолчанию — “всё разрешено”, но с флагом, чтобы можно было включить allowlist)
- В `Bot` сделать composition root (DI через `Microsoft.Extensions.Hosting`).

## Критерии приёмки
- `dotnet build` проходит.
- Базовый хост стартует и пишет в лог “Started”.

## Как проверить
- `dotnet build`
- `dotnet run --project TgCodexBridge.Bot`
