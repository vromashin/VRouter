#!/bin/bash
# Выкладка нашего сервиса с кривой, переключение на наш стек, сброс симулятора, прогон задачи.
set -x
S="sudo -S -p '' "
B=$(date +%s)

echo "=== старый стек останавливаем ==="
echo valex024771 | $S systemctl stop agv-control-api agv-fleet-manager

echo "=== наш сервис ==="
echo valex024771 | $S tar -czf /tmp/bk-workers-$B.tgz -C /opt/rms/vrp-workers .
echo valex024771 | $S systemctl stop vrp-api vrp-workers
sleep 2
echo valex024771 | $S tar -xzf /tmp/workers.tgz -C /opt/rms/vrp-workers
echo valex024771 | $S tar -xzf /tmp/api.tgz -C /opt/rms/vrp-api
echo valex024771 | $S systemctl start vrp-api vrp-workers
sleep 18

echo "=== сброс симулятора ==="
echo valex024771 | $S systemctl stop robots.simulator
sleep 3
echo valex024771 | $S cp /root/RobotsSimulatorStates.ini.bak-1795369289 /opt/rms/ntls.simulator.robotssimulator/RobotsSimulatorStates.ini
echo valex024771 | $S chown root:root /opt/rms/ntls.simulator.robotssimulator/RobotsSimulatorStates.ini
echo valex024771 | $S systemctl start robots.simulator
sleep 22
echo "sim=$(systemctl is-active robots.simulator) vrp-api=$(systemctl is-active vrp-api) vrp-workers=$(systemctl is-active vrp-workers)"

echo "=== захват и задача ==="
rm -rf /tmp/curvetest; mkdir -p /tmp/curvetest
pkill -f "mosquitto_sub.*98001" 2>/dev/null
nohup timeout 420 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 \
  -t "APR/v2/Tuskrobots/98001/order" -v > /tmp/curvetest/order.log 2>/dev/null &
sleep 2
python3 - <<'PY' > /tmp/curvetest/task.json
import json, uuid, datetime
now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
print(json.dumps({
 "taskId": str(uuid.uuid4()), "name": "curve-test", "isTemplate": False,
 "storeWmsId": "map 31.08.26", "areaWmsId": "801", "priority": 0, "status": "Created",
 "actions": [
   {"id":0,"actionOrder":1,"location":"wo-3-1","actionType":0,"taskId":"00000000-0000-0000-0000-000000000000","createdAt":now,"status":"Pending"},
   {"id":0,"actionOrder":2,"location":"wo-0-1","actionType":1,"taskId":"00000000-0000-0000-0000-000000000000","createdAt":now,"status":"Pending"}],
 "createdAt": now, "processedAt": None, "startedAt": None, "finishedAt": None, "agvId": None, "isHighestPriority": True}))
PY
date +%T
echo valex024771 | $S bash -c "rpk topic produce simple-tasks < /tmp/curvetest/task.json" 2>&1 | tail -1
sleep 180

echo "=== итог ==="
echo "orders=$(grep -c . /tmp/curvetest/order.log 2>/dev/null)"
python3 - <<'PY'
import json
for line in open('/tmp/curvetest/order.log', encoding='utf-8', errors='replace'):
    i=line.find('{')
    if i<0: continue
    try: o=json.loads(line[i:])
    except Exception: continue
    print('===', o['orderId'], 'update', o['orderUpdateId'])
    for e in o.get('edges') or []:
        t=e.get('trajectory') or {}
        if e['edgeId'] in ('801-wa148-wa104','801-wa104-wa148','801-wa187-wa148'):
            print('  ', e['edgeId'], 'degree', t.get('degree'), 'points', len(t.get('controlPoints') or []))
            for c in (t.get('controlPoints') or []): print('     ', c)
PY
echo "=== журнал симулятора ==="
echo valex024771 | $S journalctl -u robots.simulator --since "-4 min" -o cat --no-pager 2>/dev/null \
  | grep -v AuthenticationRequiredError | grep -E 'start:|Stop on|Have action|Order\(' | tail -30
echo "=== БД ==="
cat > /tmp/curvetest/q.sql <<'SQL'
\pset pager off
select "OrderId","Status","OrderUpdateId","TargetNodeId","CompletedAt","FailureReason" from "vrt_RobotOrder" order by "CreatedAt" desc limit 2;
select "SerialNumber","LastNodeId","PositionX","PositionY" from "Agvs";
SQL
echo valex024771 | $S -u postgres psql -d rms -f /tmp/curvetest/q.sql
tar -czf /tmp/curvetest.tgz -C /tmp/curvetest .
