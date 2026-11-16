# Sourced by build/install-mac.sh and store/capture-mac.sh: generate_apple_project <apple dir> <scratch dir> makes a
# fresh OneThing.xcodeproj (xcodegen lists the source files, so an old one misses new ones) and puts back the
# Support/ files as they were, since xcodegen rewrites the committed Info.plists.
generate_apple_project() {
  local apple=$1 keep=$2/Support.keep
  command -v xcodegen >/dev/null || { echo 'Needs xcodegen: brew install xcodegen'; return 1; }
  rm -rf "$keep"; cp -R "$apple/Support" "$keep"
  (cd "$apple" && xcodegen generate --quiet)
  rm -rf "$apple/Support"; cp -R "$keep" "$apple/Support"
}
