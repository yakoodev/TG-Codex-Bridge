# G2 - Parse and persist `codex_chat_id`

## Status
Not done.

## Goal
Extract Codex chat/session id from runner output and persist it to DB for future auto-resume.

## Remaining work
- Define stable parser for session id from Codex output events.
- Update `topics.codex_chat_id` when id is found.
- Keep system stable when id is absent.

## Acceptance
- At least one real run stores non-null `topics.codex_chat_id`.
- Subsequent runs can use stored id for resume.
