namespace OneThing.Cli.Tests.Reach;

/// <summary>
/// One thing the app does: the REST endpoints behind it, the <c>ot</c> commands and MCP tools that do it, and, where
/// either is missing, why (that's the documented "not available to agents, and why").
/// </summary>
/// <param name="Cli">The ot commands (first word after ot must be a real command); empty: none, see Why.</param>
/// <param name="Mcp">The MCP tools; empty: none, see Why.</param>
internal sealed record Feature(string Name, string[] Routes, string[] Cli, string[] Mcp, string? Why = null);

/// <summary>
/// What the app does, against ot and MCP. CoverageTests fails when the server maps an endpoint that isn't here, when
/// a command or tool named here doesn't exist, or when something missing has no reason. docs/ai-connectors.md shows
/// this table ("What ot and MCP cover"), made from it.
/// </summary>
internal static class Coverage
{
    // Reasons that come back.
    private const string Delete = "Deleting is the person's, in the app (nothing is ever lost): agents finish, archive or move instead, and ot can't tell a person from a shell agent.";
    private const string Plumbing = "How the app's own windows sign in and talk to the server: ot and onething mcp open the tasks folder directly and need no token.";
    private const string Person = "The person's choice, in the app";
    private const string AskAi = "Ask AI is a person chatting with an agent inside the app; an agent outside it is already connected.";
    private const string Browser = "Pairing a browser needs the person at the browser and the app at once.";
    private const string SyncSetup = "Setting up sync and choosing a side in a clash are the person's, in Settings › Sync.";
    private const string ConnectorSetup = "Signing in to another service and choosing what to connect are the person's, in Settings › Connected apps.";
    private const string NoMcpSync = "Syncing runs in the app by itself every few minutes; an agent has nothing to set or start.";

    public static readonly IReadOnlyList<Feature> Features =
    [
        // ---- Tasks -------------------------------------------------------------------------------------------------
        new("What to do now, what's waiting, the timer", ["GET /api/dashboard", "GET /api/timer"], ["ot now"], ["get_dashboard"]),
        new("Find tasks", ["GET /api/search"], ["ot list"], ["search_tasks"]),
        new("Every task", ["GET /api/items"], ["ot list --all"], ["list_tasks"]),
        new("One task", ["GET /api/items/{id}"], ["ot show", "ot open"], ["get_task"]),
        new("The task tree (board and outline)", ["GET /api/tree"], [], [],
            "ot tree and get_tree come with the Tasks tree rework (its own branch); until then ot list and list_tasks."),
        new("Add a task", ["POST /api/items"], ["ot add"], ["create_task"]),
        new("Quick capture (!! due:fri #tag @tomorrow)", ["POST /api/capture"], ["ot add"], ["capture_task"]),
        new("Change a task (title, details, priority, deadline, tags, labels, icon, color, repeat, energy, steps)", ["PATCH /api/items/{id}"], ["ot edit", "ot tag", "ot label"], ["update_task"]),
        new("Delete a task", ["DELETE /api/items/{id}"], [], [], Delete),
        new("Ordered steps", ["POST /api/items/{id}/steps"], ["ot steps"], ["add_steps"]),
        new("Done and undo", ["POST /api/items/{id}/complete", "POST /api/items/{id}/reopen"], ["ot done", "ot reopen"], ["complete_task", "reopen_task"]),
        new("Notes (with a web page), and correcting one", ["POST /api/items/{id}/notes", "PATCH /api/items/{id}/notes/{noteId}"], ["ot note", "ot note --edit"], ["add_note", "update_note"],
            "An agent corrects only notes it wrote; anyone else's gets a new note."),
        new("Snooze, and back to Do now", ["POST /api/items/{id}/schedule", "GET /api/snooze"], ["ot snooze", "ot snooze <task> clear"], ["schedule_next_action", "clear_snooze"]),
        new("Wait for another task", ["POST /api/items/{id}/after"], ["ot snooze <task> after"], ["wait_for_task"]),
        new("Tasks a task can wait for (the app's picker)", ["GET /api/items/{id}/wait-candidates"], [], [],
            "The app's picker for After another task…: ot snooze <task> after <words> and wait_for_task find the task themselves."),
        new("Reminders, and dismissing one", ["POST /api/items/{id}/reminders", "POST /api/items/{id}/reminders/{reminderId}/dismiss"], ["ot remind", "ot remind --dismiss"], ["add_reminder", "dismiss_reminder"]),
        new("Do now's order", ["POST /api/now/first", "POST /api/now/order"], ["ot first", "ot order"], ["put_first", "arrange_now"]),
        new("Next to a sibling", ["POST /api/items/{id}/reorder"], ["ot move --before", "ot move --after"], ["reorder_task"]),
        new("Under another task, top level, another group", ["POST /api/items/{id}/move"], ["ot move"], ["move_task"]),
        new("Board columns", ["POST /api/items/{id}/stage"], ["ot stage"], ["move_card"]),
        new("Archive and unarchive", ["POST /api/items/{id}/archive", "POST /api/items/{id}/unarchive"], ["ot archive", "ot unarchive"], ["archive_task", "unarchive_task"]),
        new("Archive everything finished a while ago", ["POST /api/archive"], ["ot archive --done-before"], [],
            "An agent archives one finished task at a time (archive_task), so the person sees each."),
        new("The timeline (who did what)", ["GET /api/timeline", "GET /api/items/{id}/timeline"], ["ot timeline"], ["get_report"]),

        // ---- Groups, labels, tags ----------------------------------------------------------------------------------
        new("Groups (tabs)", ["GET /api/groups"], ["ot groups"], ["list_groups"]),
        new("Make a group", ["POST /api/groups"], ["ot group add"], ["create_group"]),
        new("Rename or recolor a group", ["PATCH /api/groups/{id}"], ["ot group rename", "ot group color"], ["update_group"]),
        new("Delete a group", ["DELETE /api/groups/{id}"], [], [], Delete),
        new("Labels", ["GET /api/labels"], ["ot labels"], ["list_labels"]),
        new("Make, rename or recolor a label", ["POST /api/labels", "PATCH /api/labels/{**name}"], ["ot labels add", "ot labels rename", "ot labels color"], ["create_label", "update_label"]),
        new("Delete a label", ["DELETE /api/labels/{**name}"], [], [], Delete),
        new("Tags in use", ["GET /api/tags"], ["ot tags"], ["list_tags"]),

        // ---- Time, focus, plan, energy -----------------------------------------------------------------------------
        new("Time a task", ["POST /api/timer/start", "POST /api/timer/stop"], ["ot start", "ot stop"], ["start_timer", "stop_timer"]),
        new("Log time, and correct an entry", ["POST /api/items/{id}/time", "PUT /api/items/{id}/time/{entryId}"], ["ot log", "ot log --edit"], ["log_time", "update_time_entry"]),
        new("Remove logged time", ["DELETE /api/items/{id}/time/{entryId}"], [], [], Delete),
        new("Where the time went", ["GET /api/reports"], ["ot time"], ["get_time_report"]),
        new("The focus (Pomodoro) timer", ["POST /api/pomodoro/{action}"], ["ot focus"], ["focus_timer"]),
        new("The focus timer's lengths (focus, break, long break, sessions before it)", ["GET /api/pomodoro/settings", "PUT /api/pomodoro/settings"], ["ot focus lengths"], ["set_focus_lengths", "get_dashboard"]),
        new("The plan", ["GET /api/plan", "GET /api/plan/settings"], ["ot plan", "ot plan settings"], ["get_plan"]),
        new("Plan or unplan a task", ["POST /api/items/{id}/plan"], ["ot plan <task>"], ["plan_task"]),
        new("How the Plan view looks", ["PUT /api/plan/settings"], ["ot plan settings --view"], [], "How the Plan view looks (work hours, work days) is set in the app or with ot plan settings, also with --as (the API lets anything with the token set it); no MCP tool, get_plan reads them."),
        new("Energy", ["GET /api/energy", "PUT /api/energy"], ["ot energy"], ["get_dashboard", "set_daily_energy"]),

        // ---- Files, history, rich HTML, export ---------------------------------------------------------------------
        new("Attach a file", ["POST /api/items/{id}/attachments"], ["ot attach"], ["attach_text"]),
        new("Read an attachment", ["GET /api/items/{id}/attachments/{attachmentId}"], ["ot attachment"], ["read_attachment"]),
        new("A picture shown inside details", ["GET /api/items/{id}/embed/{name}"], [], [], "How the app shows a pasted image in a task's details: ot attachment and read_attachment get the file."),
        new("Remove an attachment", ["DELETE /api/items/{id}/attachments/{attachmentId}"], [], [], Delete),
        new("Rich HTML", ["GET /api/items/{id}/rich", "PUT /api/items/{id}/rich"], ["ot rich"], ["get_rich_html", "set_rich_html"]),
        new("Versions of the whole folder", ["GET /api/history", "GET /api/history/skipped"], ["ot history"], ["board_history"]),
        new("Save a version now", ["POST /api/history/commit"], [], [], "ot and onething mcp save a version after every change themselves."),
        new("Versions of a task, a version's text, going back", ["GET /api/items/{id}/history", "GET /api/items/{id}/history/{version}", "POST /api/items/{id}/history/{version}/restore"],
            ["ot history <task>", "ot history <task> --text", "ot restore"], ["task_history", "get_task_version", "restore_task_version"]),
        new("Export everything", ["GET /api/export"], ["ot export"], ["export_board"]),
        new("The tasks folder and its format", ["GET /api/vault"], ["ot vault", "ot vault guide"], ["vault_info"]),
        new("Obsidian vaults on this computer", ["GET /api/obsidian/vaults"], ["ot vault obsidian"], [], "For the person choosing where tasks live."),
        new("Move where the tasks live", ["PUT /api/settings/vault"], ["ot vault use"], [], Person + ": ot refuses it with --as."),

        // ---- Settings, extras, hooks -------------------------------------------------------------------------------
        new("Settings", ["GET /api/settings", "PUT /api/settings/theme"], ["ot settings", "ot settings theme"], [], Person + ": an agent has no settings tool."),
        new("Where reminders are posted (Teams)", ["PUT /api/settings"], ["ot settings teams"], [], Person + ": ot refuses it with --as."),
        new("Extras (plugins) on and off", ["GET /api/plugins", "PUT /api/plugins/{id}"], ["ot plugins", "ot plugins on", "ot plugins off"], [], Person + ": ot refuses switching with --as."),
        new("Hooks: list, add, change, on/off, remove", ["GET /api/hooks", "POST /api/hooks", "PUT /api/hooks/{id}", "POST /api/hooks/{id}/enabled", "DELETE /api/hooks/{id}"],
            ["ot hooks", "ot hooks add", "ot hooks edit", "ot hooks enable", "ot hooks remove"], ["list_hooks", "add_hook", "update_hook", "set_hook_enabled", "remove_hook"],
            "An agent's hook waits for the person's approval, and an agent changes or removes only hooks it added."),
        new("Approve or turn down a hook", ["POST /api/hooks/{id}/approve", "POST /api/hooks/{id}/reject"], ["ot hooks approve", "ot hooks reject"], [], "Only the person allows what runs on their computer: ot refuses it with --as."),
        new("Try hooks now", ["POST /api/hooks/test"], ["ot hooks test"], [], "Runs scripts on this computer now: the person's."),

        // ---- Sync and connectors -----------------------------------------------------------------------------------
        new("Sync: status and sync now", ["GET /api/plugins/sync", "POST /api/plugins/sync/now"], ["ot sync", "ot sync now"], [], NoMcpSync + " Syncing now is the person's (ot sync now refuses --as); ot sync shows the status to anyone, and runs only while the app doesn't (it syncs by itself)."),
        new("Sync: set up, clashes, devices", ["PUT /api/plugins/sync/provider", "PUT /api/plugins/sync/library", "PUT /api/plugins/sync/places", "POST /api/plugins/sync/forget", "POST /api/plugins/sync/resolve", "POST /api/plugins/sync/compare"], [], [], SyncSetup),
        new("Connectors (Notion, Microsoft To Do, GitHub): status and sync now", ["GET /api/plugins/connectors", "POST /api/plugins/connectors/{id}/sync", "GET /api/plugins/connector-github", "POST /api/plugins/connector-github/groups/{id}/sync"],
            ["ot connectors", "ot connectors sync", "ot connectors sync github"], [], NoMcpSync + " Syncing now (which starts one not syncing yet, like Start syncing in the app) is the person's: ot connectors sync refuses --as and runs only while the app doesn't; ot connectors shows the status to anyone."),
        new("Connectors: set up, sign in, preview, disconnect", [
            "PUT /api/plugins/connectors/{id}", "POST /api/plugins/connectors/{id}/preview", "DELETE /api/plugins/connectors/{id}", "PUT /api/plugins/connectors/notion/token", "GET /api/plugins/connectors/notion/databases",
            "POST /api/plugins/connectors/mstodo/signin", "GET /api/plugins/connectors/mstodo/signin", "GET /api/plugins/connectors/mstodo/lists",
            "PUT /api/plugins/connector-github/every-group", "PUT /api/plugins/connector-github/main-place", "POST /api/plugins/connector-github/groups/{id}/connect", "DELETE /api/plugins/connector-github/groups/{id}"], [], [], ConnectorSetup),

        // ---- For a person at the app -------------------------------------------------------------------------------
        new("Connect an AI app (setup snippets)", ["GET /api/plugins/connect-ai"], [], [], "Setup for the person connecting an app (this page has it too); an agent is already connected."),
        new("Ask AI (chat in the app)", [
            "GET /api/plugins/agent-chat", "POST /api/plugins/agent-chat/select", "POST /api/plugins/agent-chat/message", "POST /api/plugins/agent-chat/cancel", "POST /api/plugins/agent-chat/new",
            "POST /api/plugins/agent-chat/answer", "GET /api/plugins/agent-chat/chats", "POST /api/plugins/agent-chat/chats/{id}/open", "PUT /api/plugins/agent-chat/chats/{id}",
            "DELETE /api/plugins/agent-chat/chats/{id}", "POST /api/plugins/agent-chat/chats/{id}/restore", "GET /api/plugins/agent-chat/models", "POST /api/plugins/agent-chat/models",
            "DELETE /api/plugins/agent-chat/models/{id}", "GET /api/plugins/agent-chat/stream"], [], [], AskAi),
        new("Browser extension: pairing and devices", [
            "GET /api/plugins/browser-extension", "POST /api/plugins/browser-extension/pair", "GET /api/plugins/browser-extension/pair", "POST /api/plugins/browser-extension/claim",
            "GET /api/plugins/browser-extension/devices", "DELETE /api/plugins/browser-extension/devices/{id}", "GET /api/plugins/browser-extension/download/{browser}"], [], [], Browser),
        new("Send feedback", ["POST /api/plugins/feedback"], [], [], "A person's note to the developers, sent from the app."),

        // ---- Plumbing ----------------------------------------------------------------------------------------------
        new("Sign-in and the app's own connection", ["GET /health", "GET /auth", "POST /api/login", "POST /api/launch", "GET /api/connection"], [], [], Plumbing),
        new("Live events for the app's windows", ["GET /api/events"], [], [], "The app's windows follow changes with it; scripts use hooks (ot hooks)."),
        new("MCP over HTTP", ["GET /mcp", "POST /mcp", "DELETE /mcp"], ["onething mcp"], [], "It is the MCP server (onething mcp is the same over stdio)."),
        new("The REST API's description", ["GET /openapi/{documentName}.json"], [], [], "For HTTP-based AI tools (ChatGPT, Copilot Studio); ot and MCP describe themselves."),
    ];
}
