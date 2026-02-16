# H1 — Уведомления notify users (опционально)

## Цель
Пинговать пользователя (и список notify) на approval prompt и на завершение.

## Сделать
- Таблица `notify_users` (уже есть в B1)
- По умолчанию добавить `ALLOWED_USER_ID`
- В момент:
  - появления approval prompt
  - завершения job (success/error/cancel)
  - отправлять mention: `tg://user?id=...`

## Критерии приёмки
- На prompt и финал приходит уведомление.

## Как проверить
- Довести до prompt и до конца job → увидеть mention.
