# A2 — Docker: контейнер, volume `/data`, переменные окружения

## Цель
Запуск бота в Docker с сохранением состояния и логов.

## Сделать
- `Dockerfile` (multi-stage build)
- `docker-compose.yml`
  - volume `./data:/data`
  - переменные:
    - `BOT_TOKEN`
    - `ALLOWED_USER_ID`
    - `GROUP_CHAT_ID`
    - `STATE_DIR=/data`
    - `LOG_DIR=/data/logs`
    - `CODEX_BIN=codex`
    - `PATH_POLICY_MODE=all` (или `allowlist`)
    - `ALLOWED_ROOTS=` (опционально)
    - `CANCEL_SOFT_TIMEOUT_SEC=10`
    - `CANCEL_KILL_TIMEOUT_SEC=5`
- healthcheck (например, HTTP endpoint внутри бота **или** “живой” лог heartbeat раз в N секунд)

## Критерии приёмки
- `docker compose up -d` поднимает сервис без падений.
- После старта в `./data` появляются `state.db` и логи.

## Как проверить
- `docker compose up -d && docker compose logs -f`
- `ls -la ./data ./data/logs`
