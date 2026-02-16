# B1 — SQLite: схема + автоинициализация

## Цель
Хранить проекты, топики и контекст.

## Сделать
- SQLite БД: `STATE_DIR/state.db`
- Таблицы:
  - `projects(id, dir_path unique, created_at)`
  - `topics(id, project_id, group_chat_id, message_thread_id unique, codex_chat_id null, name, busy, status, context_left_percent null, last_job_started_at, last_job_finished_at)`
  - `notify_users(id, user_id unique)`
  - `(опц.) audit_log(id, ts, type, payload_json)`
- Индексы по `project_id`, `message_thread_id`.
- Миграции: простая версия-схема (например, таблица `schema_version`).

## Критерии приёмки
- При первом запуске БД создаётся.
- Повторный запуск не ломает схему.

## Как проверить
- `sqlite3 /data/state.db ".tables"`
- `sqlite3 /data/state.db "select * from schema_version;"`
