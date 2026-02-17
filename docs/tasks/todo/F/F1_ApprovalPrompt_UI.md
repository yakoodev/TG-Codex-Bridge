# F1 - Approval Prompt UI

## Status
Done (within current `codex exec` limitations).

## Implemented
- Bot detects command execution events and shows inline buttons:
  - `Yes`
  - `Yes always`
  - `No`
- Selection state is persisted for the active prompt.
- Prompt message is edited after selection.
- `Yes` / `Yes always` continues via bot-side rerun flow, `No` stops the task.

## Limitation
- `codex exec` (v0.101.0) still does not support true interactive `approval_policy=on-request` continuation in the same process.

## Remaining work
- Switch from rerun flow to in-session approvals when CLI supports interactive mode.
