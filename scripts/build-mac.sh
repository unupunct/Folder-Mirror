#!/usr/bin/env bash
# Builds "Folder Mirror.app" and a drag-to-install .dmg. Must run on macOS (needs sips, iconutil, codesign, hdiutil).
#   scripts/build-mac.sh [universal|osx-arm64|osx-x64] [version]   (default: universal = one dmg for Apple silicon and Intel)
set -euo pipefail

RID="${1:-universal}"
VERSION="${2:-1.1.0}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/release/mac"
PUB="$OUT/publish-$RID"
STAGE="$OUT/stage-$RID"
APP="$STAGE/Folder Mirror.app"
case "$RID" in
  osx-arm64) ARCH="arm64" ;;
  osx-x64)   ARCH="x64" ;;
  universal) ARCH="universal" ;;
  *) echo "Unsupported runtime identifier: $RID" >&2; exit 1 ;;
esac
DMG="$OUT/FolderMirror-$VERSION-macOS-$ARCH.dmg"

rm -rf "$PUB" "$STAGE" "$OUT/publish-osx-arm64" "$OUT/publish-osx-x64"
mkdir -p "$OUT" "$APP/Contents/MacOS" "$APP/Contents/Resources"

publish_rid() {
  dotnet publish "$ROOT/src/FolderMirror.Mac/FolderMirror.Mac.csproj"     -c Release -r "$1" --self-contained true     -p:Version="$VERSION" -p:AssemblyVersion="$VERSION.0" -p:DebugType=none     -o "$2"
}

if [ "$RID" = "universal" ]; then
  echo "==> Publishing osx-arm64 and osx-x64, then merging native binaries with lipo"
  publish_rid osx-arm64 "$OUT/publish-osx-arm64"
  publish_rid osx-x64 "$OUT/publish-osx-x64"
  cp -R "$OUT/publish-osx-arm64" "$PUB"
  merged=0
  while IFS= read -r -d '' f; do
    rel="${f#"$OUT/publish-osx-arm64/"}"
    other="$OUT/publish-osx-x64/$rel"
    if file -b "$f" | grep -q "Mach-O" && [ -f "$other" ]; then
      lipo -create "$f" "$other" -output "$PUB/$rel"
      merged=$((merged+1))
    fi
  done < <(find "$OUT/publish-osx-arm64" -type f -print0)
  echo "    merged $merged native files into universal binaries"
  rm -rf "$OUT/publish-osx-arm64" "$OUT/publish-osx-x64"
else
  echo "==> Publishing ($RID, self-contained: no .NET install needed on the Mac)"
  publish_rid "$RID" "$PUB"
fi

cp -R "$PUB"/. "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/FolderMirror"
sed "s/@VERSION@/$VERSION/g" "$ROOT/installer/mac/Info.plist" > "$APP/Contents/Info.plist"

echo "==> Building the app icon"
ICONSET="$OUT/FolderMirror.iconset"
rm -rf "$ICONSET"; mkdir -p "$ICONSET"
SRC="$ROOT/assets/logo.png"
for s in 16 32 128 256 512; do
  sips -z $s $s "$SRC" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) "$SRC" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/FolderMirror.icns"
rm -rf "$ICONSET"

echo "==> Ad-hoc code signing (not notarized: see README, first launch needs right-click > Open)"
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"

echo "==> Creating $DMG"
ln -s /Applications "$STAGE/Applications"
cp "$ROOT/LICENSE" "$STAGE/LICENSE.txt"
rm -f "$DMG"
hdiutil create -volname "Folder Mirror $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG"

echo "==> Done"
ls -lh "$DMG"
shasum -a 256 "$DMG"
