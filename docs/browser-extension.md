# Browser extension

See what to do now, add tasks and save notes about the page you're on, without leaving the browser. It works in
**Microsoft Edge**, **Google Chrome** (a side panel) and **Safari** on the Mac (a toolbar popup). It only talks to
Todo Tracker on your own computer.

The extension isn't in the browser stores yet, so it's set up by hand once. The app walks you through it: open
**Browser extension** (the puzzle button in the dashboard header, or **⋯ › Set up the browser extension** in the
sidebar). It opens on the steps for the browser you're using and has the download and the pairing code right there.
When the extension is in the stores, this becomes a single link.

## Edge and Chrome

1. **Download the extension** from the setup panel and unzip it into a folder you keep (the browser loads it from
   there; deleting the folder removes the extension).
2. Open `edge://extensions` (or `chrome://extensions`): browsers don't let pages link there, so the panel copies it for you.
3. Turn on **Developer mode** (Edge: left column; Chrome: top right).
4. Choose **Load unpacked** and pick the unzipped folder (the one with `manifest.json`).
5. Pin it from the puzzle-piece menu and click it: the side panel opens.
6. **Pair it:** choose **Get a pairing code** in the setup panel and type the 6 digits into the extension.

Edge may ask at startup whether to keep "developer mode extensions": choose **Keep**.

## Safari (Mac)

Safari extensions come inside a small Mac app.

1. Get **Todo Tracker Extension.app**: CI builds it on every change (the `safari-extension` artifact). To build it
   yourself (needs Xcode), download the Safari version from the setup panel, unzip it, and run
   `xcrun safari-web-extension-converter todo-tracker-extension-safari --macos-only --app-name "Todo Tracker Extension"`,
   then build and run the project Xcode opens.
2. A downloaded app that isn't notarized is blocked the first time it opens. Right-click it › **Open** › **Open** (or
   System Settings › Privacy & Security › **Open Anyway**), or in Terminal:
   `xattr -dr com.apple.quarantine "Todo Tracker Extension.app"`.
3. Safari › Settings › Advanced: turn on **Show features for web developers**. Then **Develop › Allow Unsigned
   Extensions** (until the extension is signed, Safari asks for this again after it restarts).
4. Open **Todo Tracker Extension.app** once, then Safari › Settings › Extensions: tick **Todo Tracker** and allow it on
   `127.0.0.1`.
5. Click the Todo Tracker button in the toolbar and type the pairing code.

## Pairing

The setup panel shows a **6-digit code**. The extension trades it for **its own access token**, so there's nothing
secret to copy, and the panel says **Paired with Edge on Windows** (or whichever browser) as soon as it's used. A code
works once, for 5 minutes, and five wrong tries void it. Only the extension can use a code: the request needs a header
that web pages can't send to the app.

A paired browser's token can do what the extension does and nothing more: see what to do now, add tasks, finish
and snooze them, and add notes. Everything else (deleting, settings, plugins, the AI tools over MCP, sign-in links)
is refused, including anything added to the app later, and whatever it does is recorded as the browser. **Paired browsers** in the setup panel lists them; **Remove**
takes one's access away at once (the extension then asks for a new code). Switching the plugin off (it applies after a restart, like every plugin) stops them all.
Pasting the app's own token (**⋯ › Copy API token**, under *Other server, or paste a token instead*) still works and
has full access.

## For developers

* One source in `extension/`: `manifest.json` (Edge/Chrome, side panel) and `safari/manifest.json` (Safari, popup).
  The code uses `browser` when it exists (Safari) and `chrome` otherwise. A test keeps the two manifests in step.
* The app serves the extension ready to load: `GET /api/plugins/browser-extension/download/chromium` or `/safari`.
* CI zips both, and builds the Safari app (ad-hoc signed, not notarized) with `safari-web-extension-converter` on macOS.
* An end-to-end test loads the unpacked extension in Chromium, pairs it with a code and checks it shows the tasks.
