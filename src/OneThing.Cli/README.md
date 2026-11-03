# onething (ot) — One Thing in your terminal and for AI agents

`onething`, or `ot` for short, reads and changes your One Thing tasks (markdown files in a folder; an Obsidian vault
works) without the app running, and `onething mcp` serves them to AI apps over MCP (stdio). The One Thing apps
install both names; this .NET tool installs `onething` (make `ot` an alias in your shell if you like).

```
ot                                  what to do now
ot add 'Renew passport !! due:7d #admin'
ot done passport
ot open passport                    open the task in the app (ot show prints its onething:// link)
ot hooks add taskCompleted --bash 'cat >> ~/done.jsonl'   run a script when a task is done (ot help hooks)
onething mcp                        MCP server for Claude Desktop, Claude Code, Copilot CLI, VS Code
```

Every command takes `--json` and `--as <agent>`. `ot help` lists all commands. Setup for each AI app:
https://github.com/shahabmokhtari/todo-tracker/blob/master/docs/ai-connectors.md
