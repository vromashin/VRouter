#!/bin/bash
# Сборка RobotsSimulatorService из перенесенных исходников, без components5 (нужен только GUI).
set -x
S="sudo -S -p '' "
BASE=/tmp/simbuild2
rm -rf "$BASE"
mkdir -p "$BASE"
tar -xzf /tmp/simsources.tgz -C "$BASE"
ls -la "$BASE"

echo "=== qtmqtt ==="
cd "$BASE/qtmqtt" || exit 1
/usr/lib/qt5/bin/syncqt.pl -version 5.15.15 -outdir "$BASE/qtmqtt" "$BASE/qtmqtt"
qmake qtmqtt.pro && make -j"$(nproc)" sub-src
echo valex024771 | $S make -C src install

echo "=== vulkan sdk ==="
export VULKAN_SDK=/opt/vulkan-sdk
echo valex024771 | $S mkdir -p "$VULKAN_SDK"
echo valex024771 | $S ln -sfn /usr/include "$VULKAN_SDK/Include"
echo valex024771 | $S ln -sfn "/usr/lib/$(gcc -print-multiarch)" "$VULKAN_SDK/lib"
echo valex024771 | $S ln -sfn /usr/bin "$VULKAN_SDK/bin"

echo "=== simulator ==="
export COMPONENTS5="$BASE/components5"
mkdir -p "$BASE/sim/build"
cd "$BASE/sim/build" || exit 1
qmake ../RobotsSimulator.pro && make -j"$(nproc)"

echo "=== результат ==="
ls -la "$BASE/bin_RobotsSimulator/linux/" 2>/dev/null | head -20
