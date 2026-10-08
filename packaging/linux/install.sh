#!/bin/sh
# Installs Todo Tracker for this user: the app in ~/.local/share/todo-tracker, a launcher in your menu, and (with
# --autostart) a start with your session. Run it from the unpacked folder: ./install.sh [--autostart]
set -eu

here=$(cd "$(dirname "$0")" && pwd)
app="${XDG_DATA_HOME:-$HOME/.local/share}/todo-tracker"
bin="$HOME/.local/bin"
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
icons="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"

mkdir -p "$bin" "$apps" "$icons"
# A new copy next to the old one, then swapped: files of a running Todo Tracker are never overwritten.
rm -rf "$app.new" "$app.old"
cp -R "$here/app" "$app.new"
if [ -d "$app" ]; then mv "$app" "$app.old"; fi
mv "$app.new" "$app"
rm -rf "$app.old"
chmod +x "$app/todo-tracker" "$app/tt" 2>/dev/null || true
ln -sf "$app/todo-tracker" "$bin/todo-tracker"
ln -sf "$app/tt" "$bin/tt"
cp "$here/todo-tracker.png" "$icons/todo-tracker.png"
sed "s|^Exec=.*|Exec=$app/todo-tracker|" "$here/todo-tracker.desktop" > "$apps/todo-tracker.desktop"

if [ "${1:-}" = "--autostart" ]; then
  autostart="${XDG_CONFIG_HOME:-$HOME/.config}/autostart"
  mkdir -p "$autostart"
  cp "$apps/todo-tracker.desktop" "$autostart/todo-tracker.desktop"
  echo "Todo Tracker starts with your session."
fi

echo "Installed. Start it from your app menu, or run: todo-tracker"
echo "On GNOME, tray icons need the AppIndicator extension (gnome-shell-extension-appindicator)."
