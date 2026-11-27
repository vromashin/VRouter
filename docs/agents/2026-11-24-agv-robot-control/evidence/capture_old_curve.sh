#!/bin/bash
S="sudo -S -p '' "

echo "== остановка наших сервисов =="
echo valex024771 | $S systemctl stop vrp-api vrp-workers
echo "== сброс симулятора =="
echo valex024771 | $S systemctl stop robots.simulator
sleep 3
echo valex024771 | $S cp /root/RobotsSimulatorStates.ini.bak-1795369289 /opt/rms/ntls.simulator.robotssimulator/RobotsSimulatorStates.ini
echo valex024771 | $S chown root:root /opt/rms/ntls.simulator.robotssimulator/RobotsSimulatorStates.ini
echo "== запуск старого стека =="
echo valex024771 | $S systemctl start agv-control-api agv-fleet-manager
sleep 5
echo valex024771 | $S systemctl start robots.simulator
sleep 25
echo "state: agv-control-api=$(systemctl is-active agv-control-api) fleet=$(systemctl is-active agv-fleet-manager) sim=$(systemctl is-active robots.simulator)"

echo "== захват order =="
rm -rf /tmp/oldcurve; mkdir -p /tmp/oldcurve
pkill -f "mosquitto_sub.*98001" 2>/dev/null
nohup timeout 420 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 \
  -t "APR/v2/Tuskrobots/98001/order" -v > /tmp/oldcurve/order.log 2>/dev/null &
sleep 2

python3 - <<'PY' > /tmp/oldcurve/task.json
import json, uuid, datetime
now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
print(json.dumps({
 "taskId": str(uuid.uuid4()), "name": "old-curve-capture", "isTemplate": False,
 "storeWmsId": "map 31.08.26", "areaWmsId": "801", "priority": 0, "status": "Created",
 "actions": [
   {"id":0,"actionOrder":1,"location":"wo-3-1","actionType":0,"taskId":"00000000-0000-0000-0000-000000000000","createdAt":now,"status":"Pending"},
   {"id":0,"actionOrder":2,"location":"wo-0-1","actionType":1,"taskId":"00000000-0000-0000-0000-000000000000","createdAt":now,"status":"Pending"}],
 "createdAt": now, "processedAt": None, "startedAt": None, "finishedAt": None, "agvId": None, "isHighestPriority": True}))
PY
date +%T
echo valex024771 | $S bash -c "rpk topic produce simple-tasks < /tmp/oldcurve/task.json" 2>&1 | tail -1

echo "== ждём 200 секунд =="
sleep 200
echo "orders=$(grep -c . /tmp/oldcurve/order.log 2>/dev/null)"
echo "== какие рёбра пришли =="
python3 - <<'PY'
import json
seen={}
for line in open('/tmp/oldcurve/order.log', encoding='utf-8', errors='replace'):
    i=line.find('{')
    if i<0: continue
    try:
        o=json.loads(line[i:])
    except Exception:
        continue
    for e in o.get('edges') or []:
        t=e.get('trajectory') or {}
        cps=t.get('controlPoints') or []
        seen[e['edgeId']]=(t.get('degree'), len(cps))
for k,(d,n) in sorted(seen.items()):
    print(f"{k:32s} degree={d} points={n}")
print('--- кривые (degree != 1) ---')
for k,(d,n) in sorted(seen.items()):
    if d not in (1, None):
        print('CURVE', k, d, n)
PY
tar -czf /tmp/oldcurve.tgz -C /tmp/oldcurve .
ls -la /tmp/oldcurve.tgz
