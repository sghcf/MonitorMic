#!/bin/bash
# MicStreamer 构建脚本：无需 Android Studio，直接用 android.jar + aapt2 + d8 + apksigner
set -e
cd "$(dirname "$0")"

ROOT="$(cd .. && pwd)"
JDK="$ROOT/tools/jdk/Contents/Home/bin"
BT="$ROOT/tools/sdk/platform"          # build-tools 34
ANDROID_JAR="$ROOT/tools/sdk/android-34/android.jar"
export JAVA_HOME="$ROOT/tools/jdk/Contents/Home"
export PATH="$JDK:$BT:$PATH"

rm -rf build && mkdir -p build/classes build/apk
KEYSTORE="$(pwd)/debug.keystore"   # 放在 build/ 外，避免每次构建换签名
if [ ! -f "$KEYSTORE" ]; then
    keytool -genkeypair -v -keystore "$KEYSTORE" \
        -alias androiddebugkey -keyalg RSA -keysize 2048 -validity 10000 \
        -storepass android -keypass android \
        -dname "CN=Android Debug,O=Android,C=US" 2>/dev/null
fi

echo "== 1. javac =="
javac -source 8 -target 8 -encoding UTF-8 \
    -classpath "$ANDROID_JAR" \
    -d build/classes \
    src/com/example/micstreamer/*.java

echo "== 2. d8 (classes.dex) =="
d8 --min-api 25 --output build/apk build/classes/com/example/micstreamer/*.class 2>&1 | grep -v "Warning:" || true

echo "== 3. aapt2 link (生成 base APK) =="
aapt2 link -o build/micstreamer-unsigned.apk \
    -I "$ANDROID_JAR" \
    --manifest AndroidManifest.xml \
    --min-sdk-version 25 \
    --target-sdk-version 34 \
    --version-code 4 --version-name 1.2.1

echo "== 4. 塞入 classes.dex =="
cd build/apk && zip -q -X ../micstreamer-unsigned.apk classes.dex && cd ../..

echo "== 5. 签名 =="
apksigner sign --ks "$KEYSTORE" --ks-pass pass:android \
    --key-pass pass:android --out build/micstreamer.apk build/micstreamer-unsigned.apk

echo "== 完成: $(ls -lh build/micstreamer.apk | awk '{print $5}') =="
