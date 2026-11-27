#!/bin/bash
# Третий заход: собираем только нужное сервису — ядро, плагин VDA, консоль, сервис.
# RNS-плагин требует components5 (IAR-компоненты) и стенду не нужен.
set -x
BASE=/tmp/simbuild2
export VULKAN_SDK=/opt/vulkan-sdk
export COMPONENTS5="$BASE/components5"
OUT="$BASE/bin_RobotsSimulator/linux"

mkdir -p "$OUT"

build() {
  local pro="$1" name="$2"
  local dir="$BASE/build-$name"
  rm -rf "$dir"; mkdir -p "$dir"
  ( cd "$dir" && qmake "$pro" && make -j"$(nproc)" ) || echo "FAILED: $name"
}

build "$BASE/sim/core/RobotsCore.pro"                                   core
build "$BASE/sim/assets/VDARobots/VDAForkLiftPlugin/VDAForkLiftPlugin.pro" vdaplugin
build "$BASE/sim/RobotsSimulatorConsole.pro"                            console
build "$BASE/sim/RobotsSimulatorServer.pro"                             server

echo "=== что получилось ==="
ls -la "$OUT"
