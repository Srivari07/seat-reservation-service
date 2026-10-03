# AI usage log

The assignment asks for "directed vs decided — be specific and honest". Keep this log as you go, then summarise it in WRITEUP.md.
Claude drafts the first three columns after each task. **You** fill in the last column.

| Date | Phase / task | What I asked the AI to do | What the AI produced | What I decided / changed myself |
|---|---|---|---|---|
| | Design review | Review my HLD/LLD against the assignment | Flagged: payment/gateway out of scope, decimal money, per-user idempotency scope, lock order, MySQL specifics | Chose MySQL, explicit cancel, all-or-nothing; kept/rejected: … |
| 2026-10-02 | Pre-Phase 0 KB review | Read AGENTS.md, 01, 03, 04, PLAN; summarise invariants, decisions, scope and the reserve transaction; list inconsistencies as questions without resolving them | A 14-bullet summary and 13 questions. Main ones: replay 201 vs "exactly one 201"; `id` vs `show_id`; lock order depends on the index access path, not ORDER BY; cancel violation → 409 vs 500; non-ASCII key → MySQL 1366 → 500; price overflow; retry count wording; check precedence. Drafted D-17 and the 03/04/PLAN edits from the answers. Unsure about: whether SDK 10 defaults to `.slnx`; MySqlConnector `GuidFormat` option name (both still to verify); the risk of C5 `contention` from the first-time quota upsert deadlock | |
| 2026-10-02 | Phase 0 — repo bootstrap | Plan then execute: pin the SDK, create the solution, fill `.editorconfig`/`.env.example`/`README.md`, extend `.gitignore`, verify the build | Confirmed via Microsoft's compatibility docs that `dotnet new sln` defaults to `.slnx` on .NET 10 (didn't guess). Created `global.json` (sdk 10.0.201, rollForward latestFeature), `SeatReservation.slnx`, `.editorconfig`, `.gitattributes` (proposed as an addition beyond the PLAN bullet, approved), `.env.example` matching `07-deploy.md`'s table, and a short `README.md` stub. Verified `dotnet --version` resolves to 10.0.201 and `dotnet build` succeeds. Flagged a risk for Phase 1: the Dockerfile's `dotnet/sdk:10.0` image tag must resolve to a patch ≥ 10.0.201 or the pinned rollForward will fail the container build | |
| | | | | |
