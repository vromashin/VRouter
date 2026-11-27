#!/bin/bash
NEW=/tmp/simbuild2/bin_RobotsSimulator/linux
OLD=/opt/rms/ntls.simulator.robotssimulator
echo "=== НОВОЕ ==="
ls -la "$NEW" "$NEW/assets"
echo "=== СТАРОЕ ==="
ls -la "$OLD" "$OLD/bin" "$OLD/bin/assets" 2>/dev/null
echo "=== config в исходниках ==="
ls -la /tmp/simbuild2/sim/config 2>/dev/null
echo "=== ищем pallet_types.json ==="
find /tmp/simbuild2/sim -name 'pallet_types.json' 2>/dev/null
find "$OLD" -name 'pallet_types.json' 2>/dev/null
echo "=== версии бинарников ==="
ls -la "$OLD/bin/RobotsSimulatorService" "$NEW/RobotsSimulatorService"
