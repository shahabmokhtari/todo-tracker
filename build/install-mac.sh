#!/usr/bin/env bash
# Builds the Mac app as CI does (Release, with the sync server inside, signed ad hoc) and puts it in /Applications.
# A copy built on this Mac runs here without notarization. Run from anywhere: build/install-mac.sh
# Needs Xcode, xcodegen and the .NET SDK (global.json). Your tasks stay where they are (~/Documents/OneThing).
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

cd "$repo/apple"
# xcodegen rewrites the Info.plists in Support/: keep the committed ones.
if [ ! -d OneThing.xcodeproj ]; then xcodegen generate >/dev/null && git checkout -- Support/; fi
xcodebuild -project OneThing.xcodeproj -scheme OneThing-macOS -destination 'platform=macOS' -configuration Release \
  -derivedDataPath "$work/dd" CODE_SIGNING_ALLOWED=NO build -quiet
app="$work/dd/Build/Products/Release/OneThing.app"

cd "$repo"
dotnet publish src/OneThing.Web --configuration Release --runtime "osx-$(uname -m | sed 's/x86_64/x64/')" --self-contained \
  -p:PublishSingleFile=true --output "$work/server" >/dev/null
mkdir -p "$app/Contents/Resources/Server"
cp -R "$work/server/." "$app/Contents/Resources/Server/"
rm -f "$app/Contents/Resources/Server/"*.pdb

# Sign inside out, each part with its own entitlements, so the widgets keep their sandbox and App Group.
group=$(/usr/libexec/PlistBuddy -c 'Print :WidgetAppGroup' "$app/Contents/Info.plist")
entitlements() { sed "s|\$(TeamIdentifierPrefix)group.com.onething|$group|g" "apple/Support/$1" > "$work/$1"; echo "$work/$1"; }
if [ -d "$app/Contents/Frameworks" ]; then
  find "$app/Contents/Frameworks" -maxdepth 1 \( -name '*.framework' -o -name '*.dylib' \) -exec codesign --force --sign - {} \;
fi
codesign --force --sign - --entitlements "$(entitlements Widgets-macOS.entitlements)" "$app/Contents/PlugIns/OneThingWidgets.appex"
codesign --force --sign - --entitlements "$(entitlements OneThing-macOS.entitlements)" "$app"
codesign --verify --deep "$app"

# Replace the installed copy (quit it first, so its server stops with it).
osascript -e 'quit app id "com.onething.macos"' 2>/dev/null || true
rm -rf /Applications/OneThing.app
ditto "$app" /Applications/OneThing.app
echo "Installed /Applications/OneThing.app"
open /Applications/OneThing.app
