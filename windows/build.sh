#!/bin/bash
# Build Relay for Windows, from a Mac or a PC with the .NET 10 SDK.
#
#   ./build.sh            dist/Relay-Windows-x64.zip and dist/Relay-Windows-arm64.zip
#   ./build.sh --test     run the tests first
#
# Each zip holds a single self-contained Relay.exe: nothing to install, no .NET
# needed on the PC. Your data lives in %APPDATA%\Relay, in the same format as
# the Mac app's, so a workspace.json copied between the two just works.
set -e
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/Relay.App/Relay.App.csproj)

if [[ " $* " == *" --test "* ]]; then
    echo "▸ Testing…"
    dotnet test tests/Relay.Core.Tests | tail -3
fi

mkdir -p dist
for RID in win-x64 win-arm64; do
    echo "▸ Publishing ${RID}…"
    OUT="build/$RID"
    rm -rf "$OUT"
    dotnet publish src/Relay.App -c Release -r "$RID" --self-contained \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true -p:DebugType=none \
        -o "$OUT" >/dev/null
    ZIP="dist/Relay-$VERSION-Windows-${RID#win-}.zip"
    rm -f "$ZIP"
    (cd "$OUT" && zip -q -9 "../../$ZIP" Relay.exe)
    echo "▸ $ZIP  ($(du -h "$ZIP" | cut -f1))"
done
