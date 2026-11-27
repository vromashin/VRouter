#!/bin/bash
S="sudo -S -p '' "

echo "=== порт api ==="
PORTS=$(echo valex024771 | $S ss -ltnp 2>/dev/null | grep -oE ':(5[0-9]{3}) ' | tr -d ': ' | sort -u | tr '\n' ' ')
echo "кандидаты: $PORTS"
API=""
for p in $PORTS; do
  if curl -s -m 3 "http://127.0.0.1:$p/health" > /dev/null 2>&1; then
    if curl -s -m 3 "http://127.0.0.1:$p/api/Agv" > /dev/null 2>&1; then API=$p; break; fi
  fi
done
echo "API=$API"

echo "=== захват order ==="
rm -rf /tmp/chg1; mkdir -p /tmp/chg1
pkill -f "mosquitto_sub.*98001" 2>/dev/null
nohup timeout 200 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 \
  -t "APR/v2/Tuskrobots/98001/order" -v > /tmp/chg1/order.log 2>/dev/null &
sleep 2

echo "=== вызов move-to-charge ==="
date +%T
curl -s -m 30 -X POST "http://127.0.0.1:$API/api/Order/move-to-charge/98001" -H 'Content-Type: application/json' -d '{}' | head -c 600
echo

sleep 25

echo "=== сообщения ==="
python3 - <<'PY'
import json
for line in open('/tmp/chg1/order.log', encoding='utf-8', errors='replace'):
    i=line.find('{')
    if i<0: continue
    try: o=json.loads(line[i:])
    except Exception: continue
    print('=== %s update %s, nodes=%s' % (o['orderId'], o['orderUpdateId'], len(o['nodes'])))
    for n in o['nodes']:
        acts=[a['actionType'] for a in n.get('actions') or []]
        mark = ' <== цель' if acts else ''
        print('   seq=%-3s %-12s acts=%s%s' % (n['sequenceId'], n['nodeId'], acts, mark))
    for n in o['nodes']:
        for a in n.get('actions') or []:
            print('   action', a['actionType'], a.get('blockingType'),
                  [(p['key'], p['value']) for p in a.get('actionParameters') or []])
PY

echo "=== журнал симулятора ==="
echo valex024771 | $S journalctl -u robots.simulator --since "-2 min" -o cat --no-pager 2>/dev/null \
  | grep -v AuthenticationRequiredError | grep -E 'start:|Stop on|Have action|Order\(' | tail -20

echo "=== БД ==="
cat > /tmp/chg1/q.sql <<'SQL'
\pset pager off
select "OrderId","Status","TargetNodeId","CreatedAt","CompletedAt","FailureReason" from "vrt_RobotOrder" order by "CreatedAt" desc limit 2;
SQL
echo valex024771 | $S -u postgres psql -d rms -f /tmp/chg1/q.sql
