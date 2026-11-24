#!/usr/bin/env bash
# Signs the built Mac app for download (Developer ID, hardened runtime), puts it in a DMG, notarizes and staples it:
#   build/mac-notarize.sh <OneThing.app> <signing identity> <out.dmg>
# The app is the one CI builds (Release, the server and the command line inside; see the macOS job in
# .github/workflows/ci.yml); it's signed again in place. The identity is a "Developer ID Application: …" certificate in
# the keychain (its name or SHA-1), or "-" for a dry run: signed ad hoc with the same structure and entitlements, the
# DMG built and checked, no notarization, stapling or Gatekeeper check (nothing leaves this computer).
# Notarizing needs (with a real identity) an App Store Connect API key, from the environment:
#   APPLE_TEAM_ID   the team the certificate belongs to (the widgets' App Group starts with it)
#   ASC_KEY_PATH    the key's .p8 file
#   ASC_KEY_ID      its key id
#   ASC_ISSUER_ID   its issuer id
# Inside out, each part with its own entitlements: native libraries, the .NET programs (apple/Support/Server-macOS.entitlements:
# JIT and libraries the app's team didn't sign), the widgets (their sandbox and App Group), then the app.
set -euo pipefail

if [ $# -ne 3 ]; then
  echo 'Usage: build/mac-notarize.sh <OneThing.app> <signing identity, or - for a dry run> <out.dmg>' >&2
  exit 2
fi
app=${1%/}
identity=$2
dmg=$3
repo=$(cd "$(dirname "$0")/.." && pwd)
support="$repo/apple/Support"
[ -d "$app/Contents" ] || { echo "Not an app: $app" >&2; exit 1; }

dry=false
if [ "$identity" = - ]; then dry=true; fi
if ! $dry; then
  for name in APPLE_TEAM_ID ASC_KEY_PATH ASC_KEY_ID ASC_ISSUER_ID; do
    [ -n "${!name:-}" ] || { echo "Notarizing needs $name (or sign ad hoc with -)." >&2; exit 1; }
  done
  [ -f "$ASC_KEY_PATH" ] || { echo "No API key at ASC_KEY_PATH." >&2; exit 1; }
fi

work=$(mktemp -d)
mounted=""
cleanup() {
  if [ -n "$mounted" ]; then hdiutil detach "$mounted" -quiet || true; fi
  rm -rf "$work"
}
trap cleanup EXIT

# A real signature gets a secure timestamp (notarization requires it); an ad hoc one can't have one.
if $dry; then stamp=--timestamp=none; else stamp=--timestamp; fi
sign() { codesign --force --options runtime "$stamp" --sign "$identity" "$@"; }

# The widgets' App Group: on a Mac it starts with the team's id. A build without a team (CI's) says
# group.com.onething; signed for a team, the app and its widgets look for <team>.group.com.onething.
plist="$app/Contents/Info.plist"
appex="$app/Contents/PlugIns/OneThingWidgets.appex"
if $dry; then
  group=$(/usr/libexec/PlistBuddy -c 'Print :WidgetAppGroup' "$plist")
else
  group="$APPLE_TEAM_ID.group.com.onething"
  /usr/libexec/PlistBuddy -c "Set :WidgetAppGroup $group" "$plist"
  if /usr/libexec/PlistBuddy -c 'Print :WidgetAppGroup' "$appex/Contents/Info.plist" > /dev/null 2>&1; then
    /usr/libexec/PlistBuddy -c "Set :WidgetAppGroup $group" "$appex/Contents/Info.plist"
  fi
fi
entitlements() { sed "s|\$(TeamIdentifierPrefix)group.com.onething|$group|g" "$support/$1" > "$work/$1"; echo "$work/$1"; }

# 1. Native code that isn't a bundle of its own: libraries anywhere in the app's Resources and Frameworks, and the
# .NET programs next to the server (symbolic links, like cli/ot, are signed through what they point to).
is_macho() { file -b "$1" | grep -q 'Mach-O'; }
programs=()
while IFS= read -r -d '' f; do
  is_macho "$f" || continue
  if file -b "$f" | grep -q 'executable'; then
    programs+=("$f")
  else
    sign "$f"
  fi
done < <(find "$app/Contents/Resources" "$app/Contents/Frameworks" -type f -print0 2>/dev/null)
for f in ${programs[@]+"${programs[@]}"}; do
  sign --entitlements "$support/Server-macOS.entitlements" "$f"
done
if [ -d "$app/Contents/Frameworks" ]; then
  find "$app/Contents/Frameworks" -maxdepth 1 -name '*.framework' -print0 | while IFS= read -r -d '' framework; do
    sign "$framework"
  done
fi

# 2. The widgets, then 3. the app, each with its own entitlements (never --deep: the widgets would lose theirs).
sign --entitlements "$(entitlements Widgets-macOS.entitlements)" "$appex"
sign --entitlements "$(entitlements OneThing-macOS.entitlements)" "$app"

codesign --verify --deep --strict --verbose=2 "$app"
signed=$(codesign -d --entitlements - "$appex" 2>/dev/null)
case "$signed" in *com.apple.security.app-sandbox*) ;; *) echo 'The widgets lost their sandbox' >&2; exit 1 ;; esac
case "$signed" in *"$group"*) ;; *) echo 'The widgets lost their App Group' >&2; exit 1 ;; esac
for f in "$app/Contents/Resources/Server/OneThing.Web" "$app/Contents/Resources/Server/cli/onething"; do
  flags=$(codesign -d --verbose=2 "$f" 2>&1)
  case "$flags" in *runtime*) ;; *) echo "No hardened runtime on $f" >&2; exit 1 ;; esac
  case "$(codesign -d --entitlements - "$f" 2>/dev/null)" in *com.apple.security.cs.allow-jit*) ;; *) echo "No JIT entitlement on $f" >&2; exit 1 ;; esac
done

# 4. The DMG: the app and a link to /Applications to drag it onto.
stage="$work/dmg"
mkdir -p "$stage"
ditto "$app" "$stage/$(basename "$app")"
ln -s /Applications "$stage/Applications"
rm -f "$dmg"
hdiutil create -volname 'One Thing' -srcfolder "$stage" -fs HFS+ -format UDZO -ov "$dmg" -quiet
codesign --force "$stamp" --sign "$identity" "$dmg"
codesign --verify --strict "$dmg"

# The copy in the DMG is signed as it should be, and its command line runs (from the mounted image).
mounted=$(hdiutil attach -nobrowse -readonly -noautoopen "$dmg" | awk -F'\t' '/\/Volumes\// {print $NF; exit}')
[ -n "$mounted" ] || { echo "Couldn't open $dmg" >&2; exit 1; }
codesign --verify --deep --strict "$mounted/$(basename "$app")"
"$mounted/$(basename "$app")/Contents/Resources/Server/cli/onething" --version
hdiutil detach "$mounted" -quiet
mounted=""

if $dry; then
  echo "Signed ad hoc and packaged (dry run: not notarized): $dmg"
  exit 0
fi

# 5. Notarize (waits for Apple's answer), staple the ticket to the DMG, and check it as Gatekeeper will.
result="$work/notary.json"
xcrun notarytool submit "$dmg" --key "$ASC_KEY_PATH" --key-id "$ASC_KEY_ID" --issuer "$ASC_ISSUER_ID" \
  --team-id "$APPLE_TEAM_ID" --wait --output-format json > "$result"
status=$(/usr/bin/plutil -extract status raw -o - "$result" 2>/dev/null || true)
if [ "$status" != Accepted ]; then
  echo "Notarization didn't accept it (status: ${status:-unknown}). Apple's log:" >&2
  id=$(/usr/bin/plutil -extract id raw -o - "$result" 2>/dev/null || true)
  if [ -n "$id" ]; then
    xcrun notarytool log "$id" --key "$ASC_KEY_PATH" --key-id "$ASC_KEY_ID" --issuer "$ASC_ISSUER_ID" --team-id "$APPLE_TEAM_ID" >&2 || true
  fi
  exit 1
fi
xcrun stapler staple "$dmg"
xcrun stapler validate "$dmg"
spctl -a -t open --context context:primary-signature -vv "$dmg"
echo "Signed, notarized and stapled: $dmg"
