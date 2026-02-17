# Task Status (Actual)

Updated after latest implementation in branch `feat/tasks-f`.

## Moved to done
- `E1_CodexRunner_Stream.md`
- `F2_ContextLeft_Parse.md`
- `F3_Cancel_SoftThenKill.md`

## Still todo
- `E2_AutoResume.md`
- `G2_Parse_CodexChatId.md`
- `F1_ApprovalPrompt_UI.md` (partial only)

## Important runtime note
- Current `codex exec` behavior in installed CLI (`0.101.0`) does not provide interactive `approval_policy=on-request` flow.
- Telegram approval buttons currently work via bot-level gate and rerun strategy, not in-process Codex stdin approvals.
- Therefore, `Yes` can restart work in a new Codex thread until `E2` + `G2` are completed.
