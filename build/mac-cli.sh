# Sourced by build/install-mac.sh and the macOS job in .github/workflows/ci.yml:
# put_cli_in_mac_app <repo> <OneThing.app> <runtime, e.g. osx-arm64> <scratch dir> puts the command line (onething,
# and ot, its short name) in the app, at Contents/Resources/Server/cli: next to the server, where AiSetups.FindCli
# looks, so "Connect an AI app" shows its full path and works with nothing on the PATH. ot is a link to onething
# (the same program: one copy, not two). Settings › Command line tool links both into ~/.local/bin.
# Not compressed (-p:EnableCompressionInSingleFile=false, said outright): a compressed onething crashed now and then on macOS arm64
# (AccessViolationException while reading git's output, about 1 run in 10); uncompressed it never did. It's larger
# (about 110 MB instead of 45).
# Call it before the app is signed: it signs the program itself (ad hoc), and the app's signature then seals it.
put_cli_in_mac_app() {
  local repo=$1 app=$2 runtime=$3 work=$4
  local cli="$app/Contents/Resources/Server/cli"
  dotnet publish "$repo/src/OneThing.Cli" --configuration Release --runtime "$runtime" --self-contained \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none --output "$work/mac-cli" --nologo -v q
  mkdir -p "$cli"
  cp "$work/mac-cli/onething" "$cli/onething"
  chmod +x "$cli/onething"
  ln -sfn onething "$cli/ot"
  codesign --force --sign - "$cli/onething"
}
