# Connectors: Notion, Microsoft To Do, Loop

Todo Tracker can keep one of your groups in step with a list in another app. Each connector is a plugin. Notion and
Microsoft To Do are **off** until you switch them on in **Plugins** (the puzzle button in the app, or the sidebar's
**⋯ › Plugins**), then restart the app. After that, open **Connectors** (the link button in the app's top bar).

| | Both ways | What it keeps in step |
|---|---|---|
| **Notion** | ✓ | title, done, due date, priority, tags (from the database's columns) |
| **Microsoft To Do** | ✓ | title, done, due date, importance, note |
| **Loop** | copy and paste | a checklist of a group's tasks (Loop has no API that other apps can write to) |

## How it stays safe

- **Nothing is ever deleted.** If you delete a task here, its Notion page is archived (it goes to Notion's trash,
  which you can restore from) or its To Do task is marked done. If an item disappears there, the task here stays.
- **Nothing is copied twice.** Each Notion page and To Do task carries its task's id: Notion stores it in a
  *Todo Tracker ID* column, which is added for you, and To Do stores it as a linked item. A sync that is cut short
  can never make a duplicate. Reconnecting links items back to their tasks.
- **Each field takes the side that changed.** If both sides changed the same field, the task here wins.
- **You see the first sync before it happens.** **Preview** says how many tasks would be added, sent or updated.
  Choose **Both ways**, **Only bring new ones in** or **Only send new ones out**, then **Start syncing**. From then
  on it syncs every 10 minutes, and **Sync now** syncs right away.
- **Existing tasks are never matched by title.** A task that only exists on one side is added to the other side,
  unless the direction you chose says otherwise.
- Tokens stay on the computer where you set the connector up. On Windows they are encrypted for your account; on
  other systems the file is readable only by you. **Set up each connector on one computer only.** Your other
  computers still get the tasks through sync.

Subtasks stay here. A connected task you move to another group, or make a subtask, keeps syncing. Archived tasks stop
syncing until you unarchive them.

## Notion

1. Go to [notion.so/my-integrations](https://www.notion.so/my-integrations) and create an **internal** integration
   with read and update content capabilities. Copy its token (it starts with `secret_` or `ntn_`).
2. In Notion, open your tasks database and choose **•••** › **Connections**, then add the integration.
3. In Todo Tracker's **Connectors** panel, paste the token and choose **Connect**. Then pick the database and the
   group.

Columns are recognized by their type and name:

| Field | Column |
|---|---|
| Title | the title column |
| Done | a checkbox named *Done* (or the first checkbox). If there is no checkbox: a *Status* column, where options in its *Complete* group count as done |
| Due date | a date column named *Due*, *Due date*, *Deadline* or *Date* (or the first date) |
| Priority | a select named *Priority*: options containing *low*, *high* or *critical*/*urgent* are matched; others count as normal |
| Tags | a multi-select named *Tags* or *Labels* |

## Microsoft To Do

Signing in to Microsoft needs an *app registration* (a client id). It's free and takes about two minutes:

1. Open the [Azure portal](https://portal.azure.com) › **Microsoft Entra ID** › **App registrations** › **New
   registration**.
2. **Name:** Todo Tracker. **Supported account types:** *Accounts in any organizational directory and personal
   Microsoft accounts*, so that both work and personal accounts can sign in.
3. **Redirect URI:** choose *Public client/native (mobile & desktop)* and enter
   `https://login.microsoftonline.com/common/oauth2/nativeclient`. Then choose **Register**.
4. Go to **Authentication** › **Allow public client flows** › **Yes**, then **Save**.
5. Go to **API permissions** › **Add a permission** › **Microsoft Graph** › **Delegated** › **Tasks.ReadWrite**.
6. Copy the **Application (client) ID** from **Overview**.

In the **Connectors** panel:
1. Paste the client id and choose **Save**.
2. Choose **Sign in to Microsoft**, open the link it shows, and enter the code.
3. Pick the list and the group.

A work or school account may need an administrator to approve the app once. The sign-in is kept encrypted on this
computer, and it only asks for access to your tasks.

To Do has no tags and no *critical* importance: tags stay here, and critical tasks show as important (high) there.

## Loop

Choose **Copy for Loop** (the list button in the top bar), pick a group, then choose **Copy checklist** and paste it
into a Loop page. It becomes a checklist there, and so do Teams, OneNote, GitHub and most markdown editors. Finished
tasks are left out unless you check **Include finished**.
