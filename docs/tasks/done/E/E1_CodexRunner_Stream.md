# E1 - Codex Runner Stream (Done)

## Status
Done in code.

## Implemented
- `codex` process is started with configured binary and project working directory.
- `stdout`/`stderr` are streamed and parsed into Telegram messages.
- Large output is split into safe chunks for Telegram.
- Busy-topic guard is implemented.
- Start/success/error status messages are sent to topic.

## Notes
- Current implementation uses `codex exec --json`.
- Interactive resume semantics are tracked by separate tasks (`E2`, `G2`).
