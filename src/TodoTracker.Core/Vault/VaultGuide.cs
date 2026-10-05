namespace TodoTracker.Core.Vault;

/// <summary>The <c>AGENTS.md</c> written at the vault root so any AI tool reading the folder knows the format.</summary>
internal static class VaultGuide
{
    public const string Marker = "<!-- todo-tracker-guide v1 -->";

    public const string Text = Marker + """

        # Todo Tracker vault

        This folder is a Todo Tracker task list stored as plain markdown. It works as (part of) an Obsidian vault.
        People, the Todo Tracker apps, and AI agents all edit the same files. Prefer the `tt` CLI or the Todo Tracker MCP
        tools when they are available; editing files directly is fine too.

        ## Layout

        - Each top-level folder is a **group** (a tab such as `Work` or `Personal`). Create a folder to add a group.
        - Each `.md` file inside a group folder (any depth) is one **top-level task**. Create a file to add a task.
          The file name is just a name; the task's identity is the `id` in its frontmatter (renaming the file is fine).
        - `Task name.html` next to a task is its optional **rich version** (free-form HTML for tables, layouts, colors).
        - `_attachments/<id>/` holds attached files. `.todo-tracker/` holds settings, the activity log, and the trash.
          Folders starting with `_` or `.` are not groups. Files directly in this folder (like this one) are not tasks.

        ## A task file

        ```markdown
        ---
        id: 7b0c2f9e-…            # keep as is; omit in new files (the app adds it)
        status: open              # open | done
        priority: high            # low | normal | high | critical (omit = normal)
        due: 2026-01-07T17:00     # local time; a date alone means 17:00
        scheduled: 2026-01-06     # "not before": the task waits until then (09:00 for a date alone)
        tags: [release]           # free-form; nested tags use /, e.g. infra/k8s
        labels: [Deep work]       # a few curated, colored labels
        ---
        # Ship release 2.3

        Free-form details in markdown.

        ## Steps                  # "## Subtasks" for an unordered list

        1. [x] Roll out A ✅ 2026-01-05 ^t1a2b3c
        	Indented lines are the step's details.
        	- [ ] Check dashboards #risky ⏫
        2. [ ] Roll out B 📅 2026-01-08 ⏳ 2026-01-06

        ## Notes

        > [!note] 2026-01-05 10:00 · Agent: copilot · [[#^t1a2b3c|Roll out A]]
        > deployed ring 0

        ## Attachments

        - [plan.pdf](../_attachments/7b0c2f9e/plan.pdf)
        ```

        - **Subtasks** are checkbox list items, nested with tabs, any depth. A numbered list means the steps must be
          done **in order** (the next one unlocks only after the previous is done, after `step-delay` if set).
        - Line tokens follow the Obsidian Tasks plugin: priority `🔺` critical, `⏫` high, `🔽` low; `📅` due date,
          `⏳` scheduled date, `✅` done date; `#tags`. `^id` at the end is a block id for links.
        - To **complete** something, check its box (`[x]`) or set `status: done` for the whole task.
        - To **add a note**, append a callout to `## Notes` with the local time and who you are
          (`> [!note] 2026-01-05 10:00 · Agent: <your name>`), optionally linking a subtask with `[[#^id|title]]`.
        - Leave `%%{…}%%` comments alone: they hold exact times, reminders, and ids for the app (hidden in Obsidian).
          Lines you add without them are fine; the app fills them in.
        - Anything else (extra frontmatter properties, other headings, tables, embeds) is kept as you wrote it.
        """;
}
