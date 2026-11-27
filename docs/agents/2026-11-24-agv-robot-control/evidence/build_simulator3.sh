#!/bin/bash
# Второй заход: правим раскладку (logic и vdalogic обязаны лежать в core/) и собираем.
set -x
S="sudo -S -p '' "
BASE=/tmp/simbuild2

mkdir -p "$BASE/core"
[ -d "$BASE/logic" ] && mv "$BASE/logic" "$BASE/core/logic"
[ -d "$BASE/vdalogic" ] && mv "$BASE/vdalogic" "$BASE/core/vdalogic"
ls -la "$BASE" "$BASE/core"

export VULKAN_SDK=/opt/vulkan-sdk
export COMPONENTS5="$BASE/components5"

rm -rf "$BASE/sim/build"
mkdir -p "$BASE/sim/build"
cd "$BASE/sim/build" || exit 1
qmake ../RobotsSimulator.pro && make -j"$(nproc)"

echo "=== результат ==="
ls -la "$BASE/bin_RobotsSimulator/linux/" 2>/dev/null | head -20
