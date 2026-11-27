#!/bin/bash
# Сборка свежего RobotsSimulator по схеме из .gitlab-ci.yml, целиком в /tmp/simbuild.
# Стенд не трогаем: системный пакет ставим только QtMqtt (нужен для линковки).
set -x
S="sudo -S -p '' "
BASE=/tmp/simbuild
mkdir -p "$BASE/core"
cd "$BASE" || exit 1

echo "=== диск ==="
df -h /tmp | tail -1

echo "=== пакеты ==="
echo valex024771 | $S apt-get update -qq
echo valex024771 | $S apt-get install -y --no-install-recommends \
  build-essential qtbase5-dev qtbase5-private-dev libqt5serialport5-dev \
  libqt5opengl5-dev libvulkan-dev glslang-tools

echo "=== репозитории ==="
clone() {
  local url="$1" dir="$2" branch="$3"
  if [ -d "$dir/.git" ]; then
    git -C "$dir" fetch origin "$branch" --depth 1 && git -C "$dir" reset --hard FETCH_HEAD
  else
    git clone --depth 1 --branch "$branch" "$url" "$dir"
  fi
}
clone https://git.whsystems.ru/robots/robotssimulator.git "$BASE/robotssimulator" master
clone https://git.whsystems.ru/robots/logic.git        "$BASE/core/logic"    master
clone https://git.whsystems.ru/robots/vdalogic.git     "$BASE/core/vdalogic" master
clone https://git.whsystems.ru/robots/qtmqtt.git       "$BASE/qtmqtt"        5.15.2
if [ -d "$BASE/components5/.git" ]; then
  git -C "$BASE/components5" fetch origin --depth 1 && git -C "$BASE/components5" reset --hard 043f05b43ae3b9a4761064a72269e26b8903e72d
else
  git clone https://github.com/vovaok/components5.git "$BASE/components5"
  git -C "$BASE/components5" checkout 043f05b43ae3b9a4761064a72269e26b8903e72d
fi
git -C "$BASE/qtmqtt" checkout --detach cade213f562049400da81e60762400df68d7c56b

echo "=== components5 ==="
mkdir -p "$BASE/components5/build"
( cd "$BASE/components5/build" && qmake .. && make -j"$(nproc)" ) || { echo "COMPONENTS5 FAILED"; }

echo "=== qtmqtt ==="
cd "$BASE/qtmqtt" || exit 1
/usr/lib/qt5/bin/syncqt.pl -version 5.15.15 -outdir "$BASE/qtmqtt" "$BASE/qtmqtt"
qmake qtmqtt.pro && make -j"$(nproc)" sub-src
echo valex024771 | $S make -C src install

echo "=== vulkan sdk (заглушка как в CI) ==="
export VULKAN_SDK=/opt/vulkan-sdk
echo valex024771 | $S mkdir -p "$VULKAN_SDK"
echo valex024771 | $S ln -sfn /usr/include "$VULKAN_SDK/Include"
echo valex024771 | $S ln -sfn "/usr/lib/$(gcc -print-multiarch)" "$VULKAN_SDK/lib"
echo valex024771 | $S ln -sfn /usr/bin "$VULKAN_SDK/bin"

echo "=== simulator ==="
export COMPONENTS5="$BASE/components5"
mkdir -p "$BASE/robotssimulator/build"
cd "$BASE/robotssimulator/build" || exit 1
qmake ../RobotsSimulator.pro && make -j"$(nproc)"

echo "=== результат ==="
ls -la "$BASE/bin_RobotsSimulator/linux/" 2>/dev/null | head -20
find "$BASE" -name 'RobotsSimulatorService' -newermt '-2 hours' 2>/dev/null
