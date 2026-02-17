# G2 - Parse and persist `codex_chat_id`

## Status
Done.

## Что сделано
- Добавлен парсинг `codex_chat_id` из stdout-событий раннера:
  - из JSON-полей (`chat_id`, `session_id`, `conversation_id` и camelCase-вариантов);
  - fallback-парсинг из текстовых строк (`resume/chat/session/conversation id`).
- При обнаружении id бот сохраняет его в `topics.codex_chat_id`.
- Если id не найден, выполнение продолжается без ошибок.

## Результат
- Сохранённый `codex_chat_id` используется в следующих запусках для resume.
