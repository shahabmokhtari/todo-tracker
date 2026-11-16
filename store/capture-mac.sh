#!/usr/bin/env bash
# Captures the Mac store screenshots (2880×1800) from a Debug build showing the made-up demo board: never your tasks.
# The demo runs beside an installed One Thing without touching it: its theme and window size are launch arguments, and
# the main window's saved size is put back afterwards, also when something fails. Needs xcodegen, a Retina screen and
# Screen Recording for the terminal. Then: store/frame.py
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
out="$repo/store/screenshots/mac/raw"
work=$(mktemp -d)
key="NSWindow Frame main-AppWindow-1"
saved=$(defaults read com.onething.macos "$key" 2>/dev/null || true)
restore() {
  pkill -f "$work/dd/" 2>/dev/null || true
  if [ -n "$saved" ]; then defaults write com.onething.macos "$key" "$saved"; else defaults delete com.onething.macos "$key" 2>/dev/null || true; fi
  rm -rf "$work"
}
trap restore EXIT
mkdir -p "$out"

swiftc -O -o "$work/winid" "$repo/store/winid.swift"
"$work/winid" --check

. "$repo/build/apple-project.sh"
generate_apple_project "$repo/apple" "$work"
cd "$repo/apple"
xcodebuild -project OneThing.xcodeproj -scheme OneThing-macOS -destination 'platform=macOS' -configuration Debug \
  -derivedDataPath "$work/dd" CODE_SIGNING_ALLOWED=NO build -quiet
app="$work/dd/Build/Products/Debug/OneThing.app"

# The saved frame ends with the screen's visible area; 1440×900 points is 2880×1800 pixels on a Retina screen.
screen=$(swift -e 'import AppKit; let f = NSScreen.main!.visibleFrame; print(Int(f.origin.x), Int(f.origin.y), Int(f.width), Int(f.height))')

shot() { # name view theme open
  pkill -f "$work/dd/" 2>/dev/null || true; sleep 2
  open -n -F --env ONETHING_DEMO=1 --env ONETHING_FEEDBACK_SEND=0 --env ONETHING_VIEW="$2" --env ONETHING_OPEN="$4" "$app" \
    --args -theme "$3" -ApplePersistenceIgnoreState YES -NSQuitAlwaysKeepsWindows NO -"$key" "100 40 1440 900 $screen"
  sleep 7
  local pid win size
  pid=$(pgrep -f "$work/dd/Build/Products/Debug/OneThing.app/Contents/MacOS/OneThing" | head -1 || true)
  [ -n "$pid" ] || { echo "The demo app didn't start"; exit 1; }
  # With Stage Manager a window that isn't in front is a small thumbnail: bring it forward and try again.
  for try in 1 2 3; do
    osascript -e "tell application \"System Events\" to set frontmost of (first process whose unix id is $pid) to true" >/dev/null 2>&1 || true
    sleep 2
    win=$("$work/winid" "$pid" | cut -d' ' -f1)
    screencapture -x -o -l"$win" "$out/$1.png"
    size=$(sips -g pixelWidth -g pixelHeight "$out/$1.png" | awk '/pixel/{printf $2" "}')
    [ "$size" = "2880 1800 " ] && break
    open -a "$app"; sleep 2
  done
  echo "$1: $size"
  [ "$size" = "2880 1800 " ] || { echo "Expected 2880×1800: needs a Retina screen with room for a 1440×900 window"; exit 1; }
}
shot M1-today today light 0
shot M2-details today light 1
shot M3-board board light 0
shot M4-plan plan light 0
shot M5-reports reports light 0
shot M6-today-dark today dark 0
