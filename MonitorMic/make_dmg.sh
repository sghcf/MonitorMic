#!/bin/bash
# 把 MonitorMic.app 打包成 DMG 安装包（拖放到 Applications 即完成安装）
set -e
cd "$(dirname "$0")"

APP="MonitorMic.app"
ROOT="$(cd .. && pwd)"
VERSION_FILE="$ROOT/VERSION"
[ -f "$VERSION_FILE" ] || { echo "❌ 未找到根目录 VERSION 文件。"; exit 1; }
VERSION="$(tr -d '[:space:]' < "$VERSION_FILE")"
[ -n "$VERSION" ] || { echo "❌ VERSION 为空。"; exit 1; }
DMG="MonitorMic-${VERSION}.dmg"
STAGE="build/dmg_stage"

[ -d "$APP" ] || { echo "❌ 先运行 ./build_app.sh 构建应用"; exit 1; }

echo "== 准备 DMG 内容 =="
rm -rf "$STAGE" "$DMG"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"

echo "== 生成 DMG =="
hdiutil create -volname "MonitorMic" \
    -srcfolder "$STAGE" \
    -ov -format UDZO -imagekey zlib-level=9 \
    "$DMG" >/dev/null

rm -rf "$STAGE"
echo "== 完成: $DMG ($(du -sh "$DMG" | cut -f1)) =="
