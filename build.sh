#!/bin/bash
# Build Relay.app.
#
#   ./build.sh                  build into build/Relay.app
#   ./build.sh --install        also copy to /Applications
#   ./build.sh --dmg            also package dist/Relay-<version>.dmg + .zip
#   ./build.sh --clean ...      wipe build artifacts first
#
# The binary is universal (Apple silicon + Intel), so the .dmg runs on any Mac
# with macOS 14 or later.
#
# Your collections live in ~/Library/Application Support/Relay/ and are never
# touched by this script.
set -e
cd "$(dirname "$0")"

APP="build/Relay.app"

# Remove the needle before searching: `$*` includes `$1`, so matching the
# needle against all arguments would always find itself.
has() { local needle="$1"; shift; [[ " $* " == *" $needle "* ]]; }
CLEAN=$(has --clean "$@" && echo 1 || echo 0)
INSTALL=$(has --install "$@" && echo 1 || echo 0)
DMG=$(has --dmg "$@" && echo 1 || echo 0)
VERSION=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" Resources/Info.plist)

if [ "$CLEAN" = "1" ]; then
    echo "▸ Clearing build artifacts…"
    rm -rf .build build dist .swiftpm
fi

echo "▸ Compiling (arm64 + x86_64)…"
ARCHS=(--arch arm64 --arch x86_64)
swift build -c release "${ARCHS[@]}"
BIN="$(swift build -c release "${ARCHS[@]}" --show-bin-path)/Relay"

echo "▸ Rendering icon…"
rm -rf build/Relay.iconset
mkdir -p build
swift tools/MakeIcon.swift build/Relay.iconset >/dev/null
iconutil -c icns build/Relay.iconset -o build/Relay.icns
rm -rf build/Relay.iconset

echo "▸ Assembling bundle…"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/Relay"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp build/Relay.icns "$APP/Contents/Resources/Relay.icns"
# Poppins, registered via ATSApplicationFontsPath in Info.plist.
# OFL.txt travels with the fonts — the licence requires it.
cp -R Resources/Fonts "$APP/Contents/Resources/Fonts"

# Ad-hoc signature: enough for Apple silicon to launch it. Another Mac will
# still ask for confirmation on first open (see README).
codesign --force --deep --sign - "$APP" 2>/dev/null

if [ "$DMG" = "1" ]; then
    echo "▸ Packaging v${VERSION}…"
    mkdir -p dist
    rm -f "dist/Relay-$VERSION.dmg" "dist/Relay-$VERSION.zip"

    # ditto preserves the bundle's symlinks and resource forks, which plain
    # `zip` mangles and Gatekeeper then rejects.
    ditto -c -k --sequesterRsrc --keepParent "$APP" "dist/Relay-$VERSION.zip"

    STAGE=$(mktemp -d)
    cp -R "$APP" "$STAGE/Relay.app"
    ln -s /Applications "$STAGE/Applications"
    hdiutil create -quiet -volname "Relay $VERSION" -srcfolder "$STAGE" \
        -ov -format UDZO "dist/Relay-$VERSION.dmg"
    rm -rf "$STAGE"

    echo "▸ dist/Relay-$VERSION.dmg  ($(du -h "dist/Relay-$VERSION.dmg" | cut -f1))"
    echo "▸ dist/Relay-$VERSION.zip  ($(du -h "dist/Relay-$VERSION.zip" | cut -f1))"
fi

if [ "$INSTALL" = "1" ]; then
    # Quit a running copy so the new one is what opens next.
    pkill -x Relay 2>/dev/null && sleep 1 || true
    rm -rf /Applications/Relay.app
    cp -R "$APP" /Applications/Relay.app
    echo "▸ Installed to /Applications/Relay.app"
else
    echo "▸ Built $APP"
fi
