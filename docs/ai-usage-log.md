# AI usage log

The assignment asks for "directed vs decided — be specific and honest". Keep this log as you go, then summarise it in WRITEUP.md.
Claude drafts the first three columns after each task. **You** fill in the last column.

| Date | Phase / task | What I asked the AI to do | What the AI produced | What I decided / changed myself |
|---|---|---|---|---|
| | Design review | Review my HLD/LLD against the assignment | Flagged: payment/gateway out of scope, decimal money, per-user idempotency scope, lock order, MySQL specifics | Chose MySQL, explicit cancel, all-or-nothing; kept/rejected: … |
| 2026-10-02 | Pre-Phase 0 KB review | Read AGENTS.md, 01, 03, 04, PLAN; summarise invariants, decisions, scope and the reserve transaction; list inconsistencies as questions without resolving them | A 14-bullet summary and 13 questions. Main ones: replay 201 vs "exactly one 201"; `id` vs `show_id`; lock order depends on the index access path, not ORDER BY; cancel violation → 409 vs 500; non-ASCII key → MySQL 1366 → 500; price overflow; retry count wording; check precedence. Drafted D-17 and the 03/04/PLAN edits from the answers. Unsure about: whether SDK 10 defaults to `.slnx`; MySqlConnector `GuidFormat` option name (both still to verify); the risk of C5 `contention` from the first-time quota upsert deadlock | |
| | | | | |
