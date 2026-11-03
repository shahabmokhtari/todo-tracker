# Copilot instructions

Follow [AGENTS.md](../AGENTS.md) at the repository root. In short:

- Build with `dotnet build OneThing.slnx -c Release -warnaserror` (warnings are errors); test with
  `dotnet test --solution OneThing.slnx -c Release --no-build`, `node --test tests/web/*.test.mjs extension/tests/*.test.mjs`,
  and Playwright in `tests/e2e`.
- Never run anything against the user's own tasks: use `--data <temp folder>` and `ONETHING_CLOUD=off`.
- Nothing is ever deleted for the user, every change is attributed to an `Actor`, and the same rules exist in C#, Swift
  and Kotlin (shared fixtures in `tests/fixtures`).
- After changing MCP tools or `ot` commands, regenerate `docs/mcp.md` and `docs/cli.md`:
  `ONETHING_WRITE_DOCS=1 dotnet test tests/OneThing.Cli.Tests --filter ReferenceDocsTests`.
- Text users read is plain, short and friendly; tests are written first and named as sentences.
