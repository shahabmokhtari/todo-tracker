#!/usr/bin/env bash
# Captures the Mac store screenshots (2880×1800) from a Debug build showing the made-up demo board: never your tasks.
# The demo runs beside an installed One Thing without touching it: its theme and window size are launch arguments, and
# the main window's saved size is put back afterwards. Needs Screen Recording for the terminal. Then: store/frame.py
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
out="$repo/store/screenshots/mac/raw"
work=$(mktemp -d)
trap 'pkill -f "$work/dd/" 2>/dev/null || true; rm -rf "$work"' EXIT
mkdir -p "$out"

swiftc -O -o "$work/winid" "$repo/store/winid.swift"
cd "$repo/apple"
xcodebuild -project OneThing.xcodeproj -scheme OneThing-macOS -destination 'platform=macOS' -configuration Debug \
  -derivedDataPath "$work/dd" CODE_SIGNING_ALLOWED=NO build -quiet
app="$work/dd/Build/Products/Debug/OneThing.app"

key="NSWindow Frame main-AppWindow-1"
saved=$(defaults read com.onething.macos "$key" 2>/dev/null || true)
# 1440×900 points: 2880×1800 pixels on a Retina screen.
read -r sw sh < <(swift -e 'import AppKit; let f = NSScreen.main!.frame; print(Int(f.width), Int(f.height))' 2>/dev/null || echo "1728 1117")

shot() { # name view theme open
  pkill -f "$work/dd/" 2>/dev/null || true; sleep 2
  open -n -F --env ONETHING_DEMO=1 --env ONETHING_FEEDBACK_SEND=0 --env ONETHING_VIEW="$2" --env ONETHING_OPEN="$4" "$app" \
    --args -theme "$3" -ApplePersistenceIgnoreState YES -NSQuitAlwaysKeepsWindows NO -"$key" "100 80 1440 900 0 0 $sw $sh"
  sleep 7
  pid=$(pgrep -f "$work/dd/Build/Products/Debug/OneThing.app/Contents/MacOS/OneThing" | head -1)
  osascript -e "tell application \"System Events\" to set frontmost of (first process whose unix id is $pid) to true" >/dev/null 2>&1 || true
  sleep 2
  read -r win _ < <("$work/winid" "$pid")
  screencapture -x -o -l"$win" "$out/$1.png"
  echo "$1: $(sips -g pixelWidth -g pixelHeight "$out/$1.png" | awk '/pixel/{printf $2" "}')"
}
shot M1-today today light 0
shot M2-details today light 1
shot M3-board board light 0
shot M4-plan plan light 0
shot M5-reports reports light 0
shot M6-today-dark today dark 0
pkill -f "$work/dd/" 2>/dev/null || true
if [ -n "$saved" ]; then defaults write com.onething.macos "$key" "$saved"; else defaults delete com.onething.macos "$key" 2>/dev/null || true; fi
