#!/bin/bash
S="sudo -S -p '' "
echo "=== строки relocation за 10 минут ==="
echo valex024771 | $S journalctl -u vrp-workers --since "-10 min" -o cat --no-pager 2>/dev/null \
  | grep -aiE 'RobotRelocation|переезд|кандидат|заряд|парков|резерв|попыт' \
  | grep -av 'SELECT' > /tmp/rel.txt
wc -l /tmp/rel.txt
tail -n 60 /tmp/rel.txt
