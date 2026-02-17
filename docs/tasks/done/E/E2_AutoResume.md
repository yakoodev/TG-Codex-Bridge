# E2 - Auto-resume by `codex_chat_id`

## Status
Not done.

## Goal
If a topic already has `codex_chat_id`, automatically resume the same Codex chat before processing a new prompt.

## Gaps
- `topics.codex_chat_id` is currently not populated.
- Runs start as new Codex threads.

## Acceptance
- Second prompt in same topic continues prior context.
- Bot logs a single `Resume` status when resume is used.
