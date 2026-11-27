#!/bin/bash
S="sudo -S -p '' "
API=5231

echo "=== что на 5231 ==="
curl -s -m 5 "http://127.0.0.1:$API/api/Agv" | head -c 300; echo

echo "=== захват order ==="
rm -rf /tmp/chg1; mkdir -p /tmp/chg1
pkill -f "mosquitto_sub.*98001" 2>/dev/null
nohup timeout 240 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 \
  -t "APR/v2/Tuskrobots/98001/order" -v > /tmp/chg1/order.log 2>/dev/null &
sleep 2

echo "=== POST move-to-charge ==="
date +%T
curl -s -m 30 -X POST "http://127.0.0.1:$API/api/Order/move-to-charge/98001" -H 'Content-Type: application/json' -d '{}'
echo

sleep 30

echo "=== сообщения order ==="
python3 - <<'PY'
import json
seen=0
for line in open('/tmp/chg1/order.log', encoding='utf-8', errors='replace'):
    i=line.find('{')
    if i<0: continue
    try: o=json.loads(line[i:])
    except Exception: continue
    seen+=1
    print('=== %s update %s, nodes=%s' % (o['orderId'], o['orderUpdateId'], len(o['nodes'])))
    for n in o['nodes']:
        acts=[a['actionType'] for a in n.get('actions') or []]
        print('   seq=%-3s %-12s acts=%s' % (n['sequenceId'], n['nodeId'], acts))
    for n in o['nodes']:
        for a in n.get('actions') or []:
            print('   action', a['actionType'], a.get('blockingType'),
                  [(p['key'], p['value']) for p in a.get('actionParameters') or []])
if not seen:
    print('сообщений нет')
PY

echo "=== журнал симулятора ==="
echo valex024771 | $S journalctl -u robots.simulator --since "-3 min" -o cat --no-pager 2>/dev/null \
  | grep -v AuthenticationRequiredError | grep -E 'start:|Stop on|Have action|Order\(|breakPoints' | tail -25

echo "=== журнал воркеров, переезды ==="
echo valex024771 | $S journalctl -u vrp-workers --since "-3 min" -o cat --no-pager 2>/dev/null \
  | grep -aiE 'переезд|relocation|зарядк|парков|Charging' | grep -av 'SELECT' | tail -15

echo "=== БД ==="
cat > /tmp/chg1/q.sql <<'SQL'
\pset pager off
select "OrderId","Status","OrderUpdateId","TargetNodeId","CreatedAt","CompletedAt","FailureReason" from "vrt_RobotOrder" order by "CreatedAt" desc limit 3;
select "SerialNumber","BatteryCharge","IsCharging","LastNodeId","CurrentOrderId" from "Agvs";
SQL
echo valex024771 | $S -u postgres psql -d rms -f /tmp/chg1/q.sql
