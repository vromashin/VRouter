#!/bin/bash
S="sudo -S -p '' "
mkdir -p /tmp/ourcap; rm -f /tmp/ourcap/*
echo "== switch to VRP"
echo valex024771 | $S systemctl stop agv-control-api agv-fleet-manager
echo valex024771 | $S systemctl start vrp-api vrp-workers
sleep 20
echo "vrp-api=$(systemctl is-active vrp-api) vrp-workers=$(systemctl is-active vrp-workers)"
pkill -f "mosquitto_sub.*98001" 2>/dev/null
nohup timeout 220 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 -t "APR/v2/Tuskrobots/98001/order" -v > /tmp/ourcap/order.log 2>/dev/null &
nohup timeout 220 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 -t "APR/v2/Tuskrobots/98001/instantActions" -v > /tmp/ourcap/actions.log 2>/dev/null &
sleep 2
python3 - <<'PY' > /tmp/ourcap/task.json
import json, uuid, datetime
now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
print(json.dumps({
 "taskId": str(uuid.uuid4()), "name": "our-capture", "isTemplate": False,
 "storeWmsId": "map 31.08.26", "areaWmsId": "801", "priority": 0, "status": "Created",
 "actions": [
   {"id":0,"actionOrder":1,"location":"wo-2-7","actionType":0,"taskId":"00000000-0000-0000-0000-000000000000","createdAt":now,"status":"Pending"},
   {"id":0,"actionOrder":2,"location":"wo-1-6","actionType":1,"taskId":"00000000-0000-0000-0000-000000000000","createdAt":now,"status":"Pending"}],
 "createdAt": now, "processedAt": None, "startedAt": None, "finishedAt": None, "agvId": None, "isHighestPriority": True}))
PY
echo valex024771 | $S bash -c "rpk topic produce simple-tasks < /tmp/ourcap/task.json" 2>&1 | tail -1
sleep 150
echo "orders=$(grep -c . /tmp/ourcap/order.log 2>/dev/null) actions=$(grep -c . /tmp/ourcap/actions.log 2>/dev/null)"
