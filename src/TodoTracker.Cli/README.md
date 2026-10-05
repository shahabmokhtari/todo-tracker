# tt — Todo Tracker in your terminal and for AI agents

`tt` reads and changes your Todo Tracker tasks (markdown files in a folder; an Obsidian vault works) without the app
running, and `tt mcp` serves them to AI apps over MCP (stdio).

```
tt                                  what to do now
tt add Renew passport !! due:7d #admin
tt done passport
tt mcp                              MCP server for Claude Desktop, Claude Code, Copilot CLI, VS Code
```

Every command takes `--json` and `--as <agent>`. `tt help` lists all commands. Setup for each AI app:
https://github.com/shahabmokhtari/todo-tracker/blob/master/docs/ai-connectors.md
