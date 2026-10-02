@AGENTS.md

# Claude Code specifics

## How we work

- **One phase at a time.** Work through one phase of `docs/PLAN.md` at a time. Start each phase in plan mode:
  1. Read the KB files relevant to that phase.
  2. Propose the plan: files to touch, SQL, tests, and how each invariant (I1–I10) is affected.
  3. Wait for my approval before editing.
- **Read KB files on demand.** `docs/kb/` files are deliberately not imported here, to keep context small. Open the ones listed for the phase.
- **Push back when I contradict the spec.** If I ask for something that contradicts `AGENTS.md` or the assignment, say so before doing it.
- **Don't guess .NET 10 / NuGet APIs or versions.** Check the installed package, the project file or the docs. If unsure, say so instead of inventing an API.
- **Keep diffs small.** Prefer explicit SQL in the code over clever abstractions; I must be able to explain every line in a live interview.

## Subagents (in `.claude/agents/`)

- **correctness-reviewer.** Use after any change to `Reservations/`, `Infrastructure/Db/`, `Infrastructure/Migrations/`, or any SQL string. Fix its findings, or explain why they're wrong, before proposing a commit.
- **scope-guard.** Use before adding a NuGet package, endpoint, table, column, container or background process.

## Commits

- At the end of each task, show the list of changed files and a proposed conventional commit message. Commit only after I reply "commit".
- Never amend pushed commits, squash, rebase or force-push.

## Progress tracking

- Tick the finished task in `docs/PLAN.md` (in the same commit).
- Append a draft row to `docs/ai-usage-log.md`: what I asked, what you produced, and anything you were unsure about. Leave the "What I decided / changed" column empty; I fill it in myself.

## Never

- Weaken, skip or delete a test or assertion to make it pass.
- Add `[Fact(Skip=...)]` or catch-all exception handlers.
- Hard-code secrets, connection strings or tokens.
- Change an API path, status code or error reason without updating `docs/kb/03-api-contract.md` and asking me first.
