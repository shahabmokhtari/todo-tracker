#!/bin/sh
# Installs One Thing for this user: the app in ~/.local/share/onething, a launcher in your menu, and (with
# --autostart) a start with your session. Run it from the unpacked folder: ./install.sh [--autostart]
set -eu

here=$(cd "$(dirname "$0")" && pwd)
app="${XDG_DATA_HOME:-$HOME/.local/share}/onething"
bin="$HOME/.local/bin"
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
icons="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"

mkdir -p "$bin" "$apps" "$icons"
# A new copy next to the old one, then swapped: files of a running One Thing are never overwritten.
rm -rf "$app.new" "$app.old"
cp -R "$here/app" "$app.new"
if [ -d "$app" ]; then mv "$app" "$app.old"; fi
mv "$app.new" "$app"
rm -rf "$app.old"
chmod +x "$app/onething" "$app/tt" 2>/dev/null || true
ln -sf "$app/onething" "$bin/onething"
ln -sf "$app/tt" "$bin/tt"
cp "$here/onething.png" "$icons/onething.png"
sed "s|^Exec=.*|Exec=$app/onething %u|" "$here/onething.desktop" > "$apps/onething.desktop"
# onething://task/<id> links (Copy link in the app) open the task here.
xdg-mime default onething.desktop x-scheme-handler/onething 2>/dev/null || true
update-desktop-database "$apps" 2>/dev/null || true

if [ "${1:-}" = "--autostart" ]; then
  autostart="${XDG_CONFIG_HOME:-$HOME/.config}/autostart"
  mkdir -p "$autostart"
  cp "$apps/onething.desktop" "$autostart/onething.desktop"
  echo "One Thing starts with your session."
fi

echo "Installed. Start it from your app menu, or run: onething"
echo "On GNOME, tray icons need the AppIndicator extension (gnome-shell-extension-appindicator)."
