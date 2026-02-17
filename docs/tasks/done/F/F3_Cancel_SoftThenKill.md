# F3 - `/cancel` soft cancel then kill (Done)

## Status
Done in code.

## Implemented
- `/cancel` first attempts soft stop via configured stdin command.
- Waits for soft timeout.
- If process is still alive, kills process tree.
- Busy/status/title are updated after cancellation.
