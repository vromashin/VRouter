#!/bin/bash
# Установка свежесобранного симулятора с бэкапом. Старый RNS-плагин отключаю:
# стенд использует VDA Forklift, а старый .so собран против прежнего ядра.
set -x
S="sudo -S -p '' "
OLD=/opt/rms/ntls.simulator.robotssimulator
NEW=/tmp/simbuild2/bin_RobotsSimulator/linux
B=$(date +%s)

echo "=== unit ==="
systemctl cat robots.simulator | head -20

echo "=== бэкап ==="
echo valex024771 | $S tar -czf /tmp/bk-simulator-$B.tgz -C "$OLD" .
ls -la /tmp/bk-simulator-$B.tgz

echo "=== стоп ==="
echo valex024771 | $S systemctl stop robots.simulator
sleep 3

echo "=== копирование ==="
for f in RobotsSimulatorService rssconsole librobotscore.so lidar_raytrace.spv; do
  echo valex024771 | $S cp -f "$NEW/$f" "$OLD/bin/$f"
done
echo valex024771 | $S cp -f "$NEW/assets/libVDAForkLiftPlugin.so" "$OLD/bin/assets/libVDAForkLiftPlugin.so"
echo valex024771 | $S mkdir -p "$OLD/bin/config"
echo valex024771 | $S cp -f /tmp/simbuild2/sim/config/pallet_types.json "$OLD/bin/config/pallet_types.json"

echo "=== RNS-плагин отключаю ==="
if [ -f "$OLD/bin/assets/libRNSForkLiftPlugin.so" ]; then
  echo valex024771 | $S mv "$OLD/bin/assets/libRNSForkLiftPlugin.so" "$OLD/bin/assets/libRNSForkLiftPlugin.so.off"
fi

echo "=== права ==="
echo valex024771 | $S chmod 775 "$OLD/bin/RobotsSimulatorService" "$OLD/bin/rssconsole"

echo "=== старт ==="
echo valex024771 | $S systemctl start robots.simulator
sleep 25
systemctl is-active robots.simulator
echo "=== журнал после старта ==="
echo valex024771 | $S journalctl -u robots.simulator --since "-1 min" -o cat --no-pager 2>/dev/null | grep -v AuthenticationRequiredError | tail -25
