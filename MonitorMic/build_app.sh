#!/bin/bash
# MonitorMic.app 构建脚本：swiftc 直接编译 SwiftUI 应用并打包。
# 运行前请先准备 BlackHole 2ch；显示器 APK 不内置，按需在应用内选择安装。
set -e
cd "$(dirname "$0")"

APP="MonitorMic.app"
ROOT="$(cd .. && pwd)"
VERSION_FILE="$PWD/VERSION"
if [ ! -f "$VERSION_FILE" ]; then
    echo "❌ 未找到 macOS 组件 VERSION 文件。"
    exit 1
fi
VERSION="$(tr -d '[:space:]' < "$VERSION_FILE")"
[ -n "$VERSION" ] || { echo "❌ VERSION 为空。"; exit 1; }
ARCH="$(uname -m)"

if [ ! -x /opt/homebrew/bin/adb ] && [ ! -x /usr/local/bin/adb ]; then
    echo "❌ 未找到 adb。请安装 Android platform-tools。"
    exit 1
fi

echo "== 编译 Swift 源码 =="
mkdir -p build_cache
swiftc -O -whole-module-optimization \
    -module-cache-path "$PWD/build_cache" \
    -target "${ARCH}-apple-macosx13.0" \
    -o MonitorMic \
    Sources/*.swift \
    -framework SwiftUI -framework Network -framework AVFoundation -framework AudioToolbox \
    -framework CoreAudio -framework Combine -framework Foundation -framework AppKit

echo "== 打包 .app =="
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp MonitorMic "$APP/Contents/MacOS/MonitorMic"
cp Info.plist "$APP/Contents/Info.plist"
if [ -x /opt/homebrew/bin/adb ]; then
    cp /opt/homebrew/bin/adb "$APP/Contents/Resources/adb"
else
    cp /usr/local/bin/adb "$APP/Contents/Resources/adb"
fi
cp AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
chmod +x "$APP/Contents/Resources/adb"
/usr/libexec/PlistBuddy -c "Add :CFBundleVersion string $VERSION" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Add :CFBundleShortVersionString string $VERSION" "$APP/Contents/Info.plist"
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true

echo "== Ad-hoc 签名 =="
codesign --force --deep --sign - "$APP" 2>&1 | sed 's/^/  /' || true

echo "== 完成: $(du -sh "$APP" | cut -f1)  $APP =="
