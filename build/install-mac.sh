#!/usr/bin/env bash
# Builds the Mac app as CI does (Release, with the sync server and the onething/ot command line inside, signed ad hoc)
# and puts it in /Applications.
# A copy built on this Mac runs here without notarization. Run from anywhere: build/install-mac.sh
# Needs Xcode, xcodegen and the .NET SDK (global.json). Your tasks stay where they are (~/Documents/OneThing).
# Replacing a notarized copy with this one changes its signature: macOS asks for its permissions again, and the
# widgets start empty until the app has run.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# A fresh project (it lists the source files), keeping the committed Info.plists that xcodegen rewrites.
. "$repo/build/apple-project.sh"
generate_apple_project "$repo/apple" "$work"

cd "$repo/apple"
xcodebuild -project OneThing.xcodeproj -scheme OneThing-macOS -destination 'platform=macOS' -configuration Release \
  -derivedDataPath "$work/dd" CODE_SIGNING_ALLOWED=NO build -quiet
app="$work/dd/Build/Products/Release/OneThing.app"

cd "$repo"
# Apple silicon even from a Rosetta terminal (where uname -m says x86_64).
arch=$([ "$(sysctl -n hw.optional.arm64 2>/dev/null)" = 1 ] && echo arm64 || echo x64)
dotnet publish src/OneThing.Web --configuration Release --runtime "osx-$arch" --self-contained \
  -p:PublishSingleFile=true --output "$work/server" --nologo -v q
mkdir -p "$app/Contents/Resources/Server"
cp -R "$work/server/." "$app/Contents/Resources/Server/"
rm -f "$app/Contents/Resources/Server/"*.pdb
. "$repo/build/mac-cli.sh"
put_cli_in_mac_app "$repo" "$app" "osx-$arch" "$work"

# Sign inside out, each part with its own entitlements, so the widgets keep their sandbox and App Group (as CI does).
group=$(/usr/libexec/PlistBuddy -c 'Print :WidgetAppGroup' "$app/Contents/Info.plist")
entitlements() { sed "s|\$(TeamIdentifierPrefix)group.com.onething|$group|g" "apple/Support/$1" > "$work/$1"; echo "$work/$1"; }
if [ -d "$app/Contents/Frameworks" ]; then
  find "$app/Contents/Frameworks" -maxdepth 1 \( -name '*.framework' -o -name '*.dylib' \) -exec codesign --force --sign - {} \;
fi
appex="$app/Contents/PlugIns/OneThingWidgets.appex"
codesign --force --sign - --entitlements "$(entitlements Widgets-macOS.entitlements)" "$appex"
codesign --force --sign - --entitlements "$(entitlements OneThing-macOS.entitlements)" "$app"
codesign --verify --deep "$app"
signed=$(codesign -d --entitlements - "$appex" 2>/dev/null)
case "$signed" in *com.apple.security.app-sandbox*) ;; *) echo 'The widgets lost their sandbox'; exit 1 ;; esac
case "$signed" in *"$group"*) ;; *) echo 'The widgets lost their App Group'; exit 1 ;; esac

# Quit the installed copy (its server stops with it), then swap the new one in: the old one stays if the copy fails.
osascript -e 'quit app id "com.onething.macos"' 2>/dev/null || true
ditto "$app" /Applications/.OneThing.app.new
rm -rf /Applications/OneThing.app
mv /Applications/.OneThing.app.new /Applications/OneThing.app
echo "Installed /Applications/OneThing.app"
open /Applications/OneThing.app
