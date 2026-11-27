#!/bin/bash
S="sudo -S -p '' "
mkdir -p /tmp/d2cap; rm -f /tmp/d2cap/*
pkill -f "mosquitto_sub.*98001" 2>/dev/null
nohup timeout 200 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 -t "APR/v2/Tuskrobots/98001/order" -v > /tmp/d2cap/order.log 2>/dev/null &
nohup timeout 200 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 -t "APR/v2/Tuskrobots/98001/instantActions" -v > /tmp/d2cap/actions.log 2>/dev/null &
sleep 2
python3 - <<'PY' > /tmp/d2cap/task.json
import json, uuid, datetime
now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
print(json.dumps({
 "taskId": str(uuid.uuid4()), "name": "d2-stationary", "isTemplate": False,
 "storeWmsId": "map 31.08.26", "areaWmsId": "801", "priority": 0, "status": "Created",
 "actions": [
   {"id":0,"actionOrder":1,"location":"wo-1-6","actionType":0,"taskId":"00000000-0000-0000-0000-000000000000","createdAt":now,"status":"Pending"},
   {"id":0,"actionOrder":2,"location":"wo-2-7","actionType":1,"taskId":"00000000-0000-0000-0000-000000000000","createdAt":now,"status":"Pending"}],
 "createdAt": now, "processedAt": None, "startedAt": None, "finishedAt": None, "agvId": None, "isHighestPriority": True}))
PY
echo valex024771 | $S bash -c "rpk topic produce simple-tasks < /tmp/d2cap/task.json" 2>&1 | tail -1
sleep 140
echo "orders=$(grep -c . /tmp/d2cap/order.log 2>/dev/null) actions=$(grep -c . /tmp/d2cap/actions.log 2>/dev/null)"
