#!/bin/bash
LOG=/tmp/build_sim.log
echo "=== размер лога ==="; wc -l $LOG
echo "=== qtmqtt: что было ==="
grep -nE 'qtmqtt|fatal:|Cloning into' $LOG | head -20
echo "=== components5: ошибки ==="
grep -nE '\*\*\*|Error [0-9]|error:|No such file|undefined reference' $LOG | head -30
echo "=== каталоги ==="
ls -la /tmp/simbuild/
echo "=== components5: что собралось ==="
ls /tmp/simbuild/components5/build/*.a /tmp/simbuild/components5/build/*/*.a 2>/dev/null | head
