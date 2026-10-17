# Copilot instructions

Follow [AGENTS.md](../AGENTS.md) at the repository root. In short:

- Build with `dotnet build TodoTracker.slnx -c Release -warnaserror` (warnings are errors); test with
  `dotnet test --solution TodoTracker.slnx -c Release --no-build`, `node --test tests/web/*.test.mjs extension/tests/*.test.mjs`,
  and Playwright in `tests/e2e`.
- Never run anything against the user's own tasks: use `--data <temp folder>` and `TODOTRACKER_CLOUD=off`.
- Nothing is ever deleted for the user, every change is attributed to an `Actor`, and the same rules exist in C#, Swift
  and Kotlin (shared fixtures in `tests/fixtures`).
- After changing MCP tools or `tt` commands, regenerate `docs/mcp.md` and `docs/cli.md`:
  `TT_WRITE_DOCS=1 dotnet test tests/TodoTracker.Cli.Tests --filter ReferenceDocsTests`.
- Text users read is plain, short and friendly; tests are written first and named as sentences.
