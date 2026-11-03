# Privacy

Onething keeps your tasks on your devices: markdown files in a folder you own. There's no account and no telemetry.
The apps only talk to the services you switch on (sync, connectors, an AI model you add) and, when you choose to
send a note, to our feedback relay. The same words are on the website at
<https://onething.awesomehq.app/privacy>.

## Your tasks

Your tasks, notes and pictures stay in your tasks folder and in the app's data folder on your computer or phone.
Nothing from them is sent to us, ever. Sync, connectors and AI apps only see what you connect them to, and only when
you switch them on.

## Send feedback

**Send feedback** (Settings, the menus, a shake on a phone; the **Send feedback** extra) sends a note to the people
who make Onething. It's only sent when you press **Send**.

What's sent:

* **Your note** and the kind you picked (Bug, Idea or Other).
* **Your email, only if you type one**, so we can reply.
* **Coarse facts about the app**: the platform (iOS, iPadOS, Android, macOS, Windows, Linux), the app version, the
  system version, the device model (for example `iPhone17,1` or `Pixel 9`; on a computer, the processor type such as
  `arm64`), your language, the screen size, and how you opened the sheet: `button` (a button or a menu), `shake` (a
  shake on a phone), `page` (a link on a web page) or `qr` (a QR code).

What's never sent: your tasks or anything in them, your name, any user, install or advertising id, your IP address as
data, your location, screenshots or logs.

Where it goes: the note goes to `issues.awesomehq.app`, a small relay that files it as an issue on Onething's GitHub
repository ([shahabmokhtari/todo-tracker](https://github.com/shahabmokhtari/todo-tracker/issues)). **That repository
is public: the issue shows your note, the facts above and, if you typed one, your email (as "Reply-to").** Leave the
email out if you don't want it there. The relay keeps your IP address only in memory, to limit how many notes can be
sent a minute, and deletes its own copy of the note 30 days after filing it (90 days if it couldn't be filed); the
issue stays on GitHub until it's deleted there. The web app and the Windows and Linux apps send the note through Onething on your
computer; the phone and Mac apps send it themselves. None of them use a GitHub account or token.

What it's used for: support (reading your note and, if you gave an email, replying) and making the app work better.
It's not used for tracking or advertising. The note isn't tied to an account or an id; the only thing that can say
who you are is the email, if you type one.

Don't want it at all? Nothing is sent unless you write a note and press **Send**. On the web and desktop apps you can
also turn off **Send feedback** in **Settings › Extras**, which takes it out of every window. On a phone, **Shake to
send feedback** in Settings only stops a shake from opening the sheet; the **Send feedback** button stays.

## Store privacy labels

What the store forms should say, from the above (Onething collects nothing else):

| Store | Answer |
| --- | --- |
| **App Store** (App Privacy) | *Data Linked to You*: **Contact Info › Email Address** (optional; App Functionality). *Data Not Linked to You*: **User Content › Customer Support** (the feedback note; App Functionality) and **Diagnostics › Other Diagnostic Data** (app and system version, device model, language, screen size; App Functionality). Not used for tracking. |
| **Google Play** (Data safety) | Collected, not shared: **Personal info › Email address** (optional; App functionality) and **App activity › Other user-generated content** (the note; App functionality), plus **App info and performance › Diagnostics** (App functionality). Sent over HTTPS. Users can choose not to send any (and to leave the email out). |
