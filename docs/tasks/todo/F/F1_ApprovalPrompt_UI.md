# F1 - Approval Prompt UI (Partial)

## Status
Partially done.

## Implemented
- Bot detects command execution events and can show inline buttons:
  - `Yes`
  - `Yes always`
  - `No`
- Button state is persisted for the active prompt and message is edited after selection.

## Current limitation
- `codex exec` (v0.101.0) does not provide interactive `approval_policy=on-request` behavior.
- Because of this, current flow uses a bot-level approval gate and reruns the job after approval.
- This means `Yes` does not continue the same Codex session in-place.

## Remaining work
- Replace rerun flow with true in-session approval once runner moves to interactive mode.
- Keep Telegram buttons as UI layer over real in-session approvals.
