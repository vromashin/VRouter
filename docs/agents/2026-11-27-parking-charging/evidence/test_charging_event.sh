#!/bin/bash
S="sudo -S -p '' "

echo "=== состояние на зарядке ==="
cat > /tmp/chg3.sql <<'SQL'
\pset pager off
select "OrderId","Status","TargetNodeId","CompletedAt" from "vrt_RobotOrder" order by "CreatedAt" desc limit 2;
select "SerialNumber","BatteryCharge","IsCharging","LastNodeId","CurrentOrderId","PositionX","PositionY" from "Agvs";
SQL
echo valex024771 | $S -u postgres psql -d rms -f /tmp/chg3.sql

echo "=== actions робота из state ==="
timeout 20 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 -t "APR/v2/Tuskrobots/98001/state" -C 1 2>/dev/null > /tmp/chg3-state.json
python3 - <<'PY'
import json
try:
    d=json.load(open('/tmp/chg3-state.json'))
    print('lastNode', d.get('lastNodeId'), 'driving', d.get('driving'),
          'battery', (d.get('batteryState') or {}).get('batteryCharge'),
          'charging', (d.get('batteryState') or {}).get('charging'))
    print('actions', [(a.get('actionType'), a.get('actionStatus')) for a in d.get('actionStates') or []])
    print('errors', [e.get('errorDescription') for e in d.get('errors') or []])
except Exception as e:
    print('state не прочитан:', e)
PY

echo "=== подписка на kafka agv-charging-completed ==="
pkill -f "rpk topic consume agv-charging-completed" 2>/dev/null
nohup timeout 120 bash -c 'echo valex024771 | sudo -S -p "" rpk topic consume agv-charging-completed -o end -f "%o %k %v\n" 2>/dev/null' > /tmp/chg3-kafka.log 2>&1 &
sleep 3

echo "=== ставим заряд 85% ==="
date +%T
curl -s -m 20 -X POST "http://127.0.0.1:5020/charge?value=85&mapId=801&serial=98001"
echo

sleep 40

echo "=== событие в kafka ==="
cat /tmp/chg3-kafka.log 2>/dev/null | head -5

echo "=== состояние после ==="
timeout 20 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 -t "APR/v2/Tuskrobots/98001/state" -C 1 2>/dev/null > /tmp/chg3-state2.json
python3 - <<'PY'
import json
try:
    d=json.load(open('/tmp/chg3-state2.json'))
    b=d.get('batteryState') or {}
    print('battery', b.get('batteryCharge'), 'charging', b.get('charging'))
except Exception as e:
    print('state не прочитан:', e)
PY

echo "=== журнал воркера про заряд ==="
echo valex024771 | $S journalctl -u vrp-workers --since "-4 min" -o cat --no-pager 2>/dev/null \
  | grep -aiE 'заряд|charging|событ' | grep -av 'SELECT' | tail -12
