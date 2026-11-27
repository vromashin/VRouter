#!/bin/bash
S="sudo -S -p '' "

echo "== сброс симулятора =="
echo valex024771 | $S systemctl stop robots.simulator && echo stopped
sleep 3
echo valex024771 | $S cp /root/RobotsSimulatorStates.ini.bak-1795369289 /opt/rms/ntls.simulator.robotssimulator/RobotsSimulatorStates.ini
echo valex024771 | $S chown root:root /opt/rms/ntls.simulator.robotssimulator/RobotsSimulatorStates.ini
echo valex024771 | $S systemctl start robots.simulator && echo started
sleep 20
echo "sim=$(systemctl is-active robots.simulator)"

echo "== состояние после сброса =="
timeout 25 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 \
  -t "APR/v2/Tuskrobots/98001/state" -C 1 > /tmp/rep-state0.json 2>/dev/null
python3 - <<'PY'
import json
try:
    d=json.load(open('/tmp/rep-state0.json'))
    p=d.get('agvPosition') or {}
    print('orderId',d.get('orderId'),'lastNode',d.get('lastNodeId'),'driving',d.get('driving'),
          'pos',round(p.get('x',0),4),round(p.get('y',0),4))
except Exception as e:
    print('state не пришёл:',e)
PY

echo "== захват и запуск задачи =="
rm -rf /tmp/rep; mkdir -p /tmp/rep
pkill -f "mosquitto_sub.*98001" 2>/dev/null
nohup timeout 400 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 \
  -t "APR/v2/Tuskrobots/98001/order" -v > /tmp/rep/order.log 2>/dev/null &
sleep 2
python3 - <<'PY' > /tmp/rep/task.json
import json, uuid, datetime
now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
print(json.dumps({
 "taskId": str(uuid.uuid4()), "name": "repro-wo-3-1", "isTemplate": False,
 "storeWmsId": "map 31.08.26", "areaWmsId": "801", "priority": 0, "status": "Created",
 "actions": [
   {"id":0,"actionOrder":1,"location":"wo-3-1","actionType":0,"taskId":"00000000-0000-0000-0000-000000000000","createdAt":now,"status":"Pending"},
   {"id":0,"actionOrder":2,"location":"wo-0-1","actionType":1,"taskId":"00000000-0000-0000-0000-000000000000","createdAt":now,"status":"Pending"}],
 "createdAt": now, "processedAt": None, "startedAt": None, "finishedAt": None, "agvId": None, "isHighestPriority": True}))
PY
date +%T
echo valex024771 | $S bash -c "rpk topic produce simple-tasks < /tmp/rep/task.json" 2>&1 | tail -1

echo "== ждём 150 секунд =="
sleep 150

echo "== итог =="
echo "orders=$(grep -c . /tmp/rep/order.log 2>/dev/null)"
timeout 20 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 \
  -t "APR/v2/Tuskrobots/98001/state" -C 1 > /tmp/rep/state-end.json 2>/dev/null
python3 - <<'PY'
import json
try:
    d=json.load(open('/tmp/rep/state-end.json'))
    p=d.get('agvPosition') or {}
    print('orderId',d.get('orderId'),'upd',d.get('orderUpdateId'),'driving',d.get('driving'),
          'lastNode',d.get('lastNodeId'),'seq',d.get('lastNodeSequenceId'),
          'pos',round(p.get('x',0),4),round(p.get('y',0),4))
    print('errors',[e.get('errorDescription') for e in d.get('errors') or []])
    print('actions',[(a.get('actionType'),a.get('actionStatus')) for a in d.get('actionStates') or []])
except Exception as e:
    print('state не пришёл:',e)
PY

echo "== журнал симулятора после сброса =="
echo valex024771 | $S journalctl -u robots.simulator --since "-9 min" -o short-iso --no-pager 2>/dev/null \
  | grep -v AuthenticationRequiredError > /tmp/rep/sim.log
wc -l /tmp/rep/sim.log
tail -n 45 /tmp/rep/sim.log

tar -czf /tmp/rep.tgz -C /tmp/rep .
ls -la /tmp/rep.tgz
