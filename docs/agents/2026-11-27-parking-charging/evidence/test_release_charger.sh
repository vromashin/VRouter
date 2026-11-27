#!/bin/bash
S="sudo -S -p '' "
API=5231

echo "=== ждём прибытия и зарядки (150 с) ==="
sleep 150
cat > /tmp/q.sql <<'SQL'
\pset pager off
select "OrderId","Status","TargetNodeId","CompletedAt" from "vrt_RobotOrder" order by "CreatedAt" desc limit 2;
select "SerialNumber","BatteryCharge","LastNodeId" from "Agvs";
SQL
echo valex024771 | $S -u postgres psql -d rms -f /tmp/q.sql

echo "=== уровень заряда на станции ==="
curl -s -m 20 -X POST "http://127.0.0.1:5020/charge?value=90&mapId=801&serial=98001"
echo

echo "=== ждём 100 с: заряженный робот должен уехать в парковку ==="
sleep 100
cat > /tmp/q2.sql <<'SQL'
\pset pager off
select "OrderId","Status","TargetNodeId","CreatedAt","CompletedAt" from "vrt_RobotOrder" order by "CreatedAt" desc limit 3;
select "SerialNumber","BatteryCharge","LastNodeId" from "Agvs";
SQL
echo valex024771 | $S -u postgres psql -d rms -f /tmp/q2.sql

echo "=== журнал воркера ==="
echo valex024771 | $S journalctl -u vrp-workers --since "-7 min" -o cat --no-pager 2>/dev/null \
  | grep -aiE 'переезд|relocation|заряд|парков|опубликовано' | grep -av 'SELECT' | tail -15
