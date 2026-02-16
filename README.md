# TG-Codex-Bridge

Каркас решения для Telegram-бота на C# по документации в `docs/`.

## Структура
- `src/TgCodexBridge.Bot` - точка входа и hosted service
- `src/TgCodexBridge.Core` - доменные интерфейсы и модели
- `src/TgCodexBridge.Infrastructure` - реализации инфраструктуры (заглушки для старта)
- `tests/TgCodexBridge.Tests` - тестовый проект

## Локальный запуск
```bash
dotnet build

dotnet run --project src/TgCodexBridge.Bot
```

## Docker
```bash
cp .env.example .env
docker compose up -d --build
```

После старта в `./data` должны появиться:
- `state.db`
- `heartbeat`
- `logs/app.log`
