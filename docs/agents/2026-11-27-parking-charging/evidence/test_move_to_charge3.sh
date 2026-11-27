#!/bin/bash
S="sudo -S -p '' "
API=5231

echo "=== состояние до ==="
cat > /tmp/chg2a.sql <<'SQL'
\pset pager off
select "OrderId","Status","TargetNodeId","CompletedAt" from "vrt_RobotOrder" order by "CreatedAt" desc limit 2;
select "SerialNumber","BatteryCharge","IsCharging","LastNodeId","CurrentOrderId","PositionX","PositionY" from "Agvs";
SQL
echo valex024771 | $S -u postgres psql -d rms -f /tmp/chg2a.sql

echo "=== захват order и instantActions ==="
rm -rf /tmp/chg2; mkdir -p /tmp/chg2
pkill -f "mosquitto_sub.*98001" 2>/dev/null
nohup timeout 260 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 \
  -t "APR/v2/Tuskrobots/98001/order" -v > /tmp/chg2/order.log 2>/dev/null &
nohup timeout 260 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 \
  -t "APR/v2/Tuskrobots/98001/instantActions" -v > /tmp/chg2/actions.log 2>/dev/null &
sleep 2

echo "=== POST move-to-charge ==="
date +%T
curl -s -m 30 -X POST "http://127.0.0.1:$API/api/Order/move-to-charge/98001" -H 'Content-Type: application/json' -d '{}'
echo

echo "=== ждём 120 с (дорога до ch1) ==="
sleep 120

echo "=== сообщения order ==="
python3 - <<'PY'
import json
seen=0
for line in open('/tmp/chg2/order.log', encoding='utf-8', errors='replace'):
    i=line.find('{')
    if i<0: continue
    try: o=json.loads(line[i:])
    except Exception: continue
    seen+=1
    print('=== %s update %s, nodes=%s' % (o['orderId'], o['orderUpdateId'], len(o['nodes'])))
    for n in o['nodes'][-4:]:
        acts=[(a['actionType'], a.get('blockingType')) for a in n.get('actions') or []]
        print('   ...seq=%-3s %-10s acts=%s' % (n['sequenceId'], n['nodeId'], acts))
    for n in o['nodes']:
        for a in n.get('actions') or []:
            print('   action', a['actionType'], a.get('blockingType'),
                  [(p['key'], p['value']) for p in a.get('actionParameters') or []])
if not seen: print('сообщений нет')
PY

echo "=== instantActions ==="
head -c 400 /tmp/chg2/actions.log

echo
echo "=== журнал симулятора ==="
echo valex024771 | $S journalctl -u robots.simulator --since "-4 min" -o cat --no-pager 2>/dev/null \
  | grep -v AuthenticationRequiredError | grep -E 'Have action|Stop on Node|start:' | tail -12

echo "=== БД после ==="
cat > /tmp/chg2b.sql <<'SQL'
\pset pager off
select "OrderId","Status","TargetNodeId","CompletedAt","FailureReason" from "vrt_RobotOrder" order by "CreatedAt" desc limit 2;
select "SerialNumber","BatteryCharge","IsCharging","LastNodeId","CurrentOrderId","PositionX","PositionY" from "Agvs";
SQL
echo valex024771 | $S -u postgres psql -d rms -f /tmp/chg2b.sql
