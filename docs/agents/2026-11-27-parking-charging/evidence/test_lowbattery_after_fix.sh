#!/bin/bash
# Проверка триггера низкого заряда после правки: робот в парковке, заряд 15%.
set -x
S="sudo -S -p '' "
B=$(date +%s)

echo "=== выкладка ==="
echo valex024771 | $S tar -czf /tmp/bk-workers-$B.tgz -C /opt/rms/vrp-workers .
echo valex024771 | $S systemctl stop vrp-workers
sleep 2
echo valex024771 | $S tar -xzf /tmp/workers.tgz -C /opt/rms/vrp-workers
echo valex024771 | $S systemctl start vrp-workers
sleep 15
echo "workers=$(systemctl is-active vrp-workers)"

echo "=== исходное состояние ==="
cat > /tmp/q.sql <<'SQL'
\pset pager off
select "OrderId","Status","TargetNodeId","CreatedAt" from "vrt_RobotOrder" order by "CreatedAt" desc limit 2;
select "SerialNumber","BatteryCharge","LastNodeId","CurrentOrderId" from "Agvs";
SQL
echo valex024771 | $S -u postgres psql -d rms -f /tmp/q.sql

echo "=== ставим заряд 15% ==="
date +%T
curl -s -m 20 -X POST "http://127.0.0.1:5020/charge?value=15&mapId=801&serial=98001"
echo

echo "=== ждём 60 с ==="
sleep 60

echo "=== результат: должен появиться заказ до ch1 ==="
cat > /tmp/q2.sql <<'SQL'
\pset pager off
select "OrderId","Status","TargetNodeId","CreatedAt" from "vrt_RobotOrder" order by "CreatedAt" desc limit 3;
select "SerialNumber","BatteryCharge","LastNodeId","CurrentOrderId" from "Agvs";
SQL
echo valex024771 | $S -u postgres psql -d rms -f /tmp/q2.sql

echo "=== решения воркера ==="
echo valex024771 | $S journalctl -u vrp-workers --since "-3 min" -o cat --no-pager 2>/dev/null \
  | grep -aiE 'переезд|relocation|заряд|парков|резерв' | grep -av 'SELECT' | tail -12
