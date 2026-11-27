#!/bin/bash
S="sudo -S -p '' "
mkdir -p /tmp/oldcap_rev; rm -f /tmp/oldcap_rev/*
pkill -f "mosquitto_sub.*98001" 2>/dev/null
nohup timeout 220 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 -t "APR/v2/Tuskrobots/98001/order" -v > /tmp/oldcap_rev/order.log 2>/dev/null &
nohup timeout 220 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 -t "APR/v2/Tuskrobots/98001/instantActions" -v > /tmp/oldcap_rev/actions.log 2>/dev/null &
sleep 2
python3 - <<'PY' > /tmp/oldcap_rev/task.json
import json, uuid, datetime
now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
print(json.dumps({
 "taskId": str(uuid.uuid4()), "name": "old-capture-rev", "isTemplate": False,
 "storeWmsId": "map 31.08.26", "areaWmsId": "801", "priority": 0, "status": "Created",
 "actions": [
   {"id":0,"actionOrder":1,"location":"wo-1-6","actionType":0,"taskId":"00000000-0000-0000-0000-000000000000","createdAt":now,"status":"Pending"},
   {"id":0,"actionOrder":2,"location":"wo-2-7","actionType":1,"taskId":"00000000-0000-0000-0000-000000000000","createdAt":now,"status":"Pending"}],
 "createdAt": now, "processedAt": None, "startedAt": None, "finishedAt": None, "agvId": None, "isHighestPriority": True}))
PY
echo valex024771 | $S bash -c "rpk topic produce simple-tasks < /tmp/oldcap_rev/task.json" 2>&1 | tail -1
sleep 150
echo "orders=$(grep -c . /tmp/oldcap_rev/order.log 2>/dev/null) actions=$(grep -c . /tmp/oldcap_rev/actions.log 2>/dev/null)"
