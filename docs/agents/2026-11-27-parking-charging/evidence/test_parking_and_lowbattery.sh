#!/bin/bash
S="sudo -S -p '' "
API=5231

echo "=== POST move-to-parking (робот на ch1) ==="
date +%T
curl -s -m 30 -X POST "http://127.0.0.1:$API/api/Order/move-to-parking/98001" -H 'Content-Type: application/json' -d '{}'
echo

echo "=== ждём дорогу до pa1 (150 с) ==="
sleep 150

cat > /tmp/q.sql <<'SQL'
\pset pager off
select "OrderId","Status","TargetNodeId","CreatedAt","CompletedAt","FailureReason" from "vrt_RobotOrder" order by "CreatedAt" desc limit 3;
select "SerialNumber","BatteryCharge","IsCharging","LastNodeId","PositionX","PositionY" from "Agvs";
SQL
echo valex024771 | $S -u postgres psql -d rms -f /tmp/q.sql

echo "=== ставим заряд 15% (робот в парковке) ==="
date +%T
curl -s -m 20 -X POST "http://127.0.0.1:5020/charge?value=15&mapId=801&serial=98001"
echo

echo "=== ждём реакцию воркера (60 с) ==="
sleep 60

cat > /tmp/q2.sql <<'SQL'
\pset pager off
select "OrderId","Status","TargetNodeId","CreatedAt","FailureReason" from "vrt_RobotOrder" order by "CreatedAt" desc limit 3;
select "SerialNumber","BatteryCharge","LastNodeId","CurrentOrderId" from "Agvs";
SQL
echo valex024771 | $S -u postgres psql -d rms -f /tmp/q2.sql

echo "=== решения воркера ==="
echo valex024771 | $S journalctl -u vrp-workers --since "-6 min" -o cat --no-pager 2>/dev/null \
  | grep -aiE 'переезд|relocation|заряд|парков|попытк|резерв|заказ ORD_0021' | grep -av 'SELECT' | tail -25
