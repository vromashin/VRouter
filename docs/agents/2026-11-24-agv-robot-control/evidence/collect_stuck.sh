#!/bin/bash
S="sudo -S -p '' "
OUT=/tmp/stuck
mkdir -p $OUT; rm -f $OUT/*

# 1. Текущее состояние робота (одно сообщение с топика state)
timeout 25 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 \
  -t "APR/v2/Tuskrobots/98001/state" -C 1 > $OUT/robot-state.json 2>/dev/null

# 2. Заказы и узлы из БД
echo valex024771 | $S -u postgres psql -d rms -P pager=off > $OUT/db-orders.txt 2>&1 <<'SQL'
select "OrderId","Status","OrderUpdateId","TargetNodeId","AgvSerialNumber","CreatedAt","SentAt","AcceptedAt","CompletedAt","FailureReason"
from "vrt_RobotOrder" order by "CreatedAt" desc limit 6;
select "OrderId","SequenceId","NodeId","ActionCount","Reached","Released"
from "vrt_RobotOrderNode"
where "OrderId" in (select "OrderId" from "vrt_RobotOrder" order by "CreatedAt" desc limit 2)
order by "OrderId","SequenceId";
select "SerialNumber","HasLoad","CurrentOrderId","LastNodeId","PositionX","PositionY","Theta" from "Agvs";
SQL

# 3. Журнал воркеров за последний час, без SQL-шума
echo valex024771 | $S journalctl -u vrp-workers --since "-60 min" --no-pager 2>/dev/null \
  | grep -avE 'Executed DbCommand|SELECT |UPDATE |INSERT |DELETE |FROM |WHERE |LIMIT|VALUES \(|JOIN |AS a|ORDER BY|^\s+(u|v|e|n|w|o)\.|Parameters=' \
  > $OUT/workers.log

# 4. События задач
echo valex024771 | $S timeout 30 rpk topic consume tasks-events -o end -n 12 -f '%o %v' 2>/dev/null > $OUT/tasks-events.log

# 5. Файл состояния симулятора
echo valex024771 | $S cat /opt/rms/ntls.simulator.robotssimulator/RobotsSimulatorStates.ini > $OUT/simulator-state.ini 2>/dev/null

# 6. Сводка
{
  echo "== когда снято =="; date
  echo; echo "== размеры =="; ls -la $OUT
  echo; echo "== робот: кратко =="
  python3 - <<'PY' 2>/dev/null
import json
try:
    d=json.load(open('/tmp/stuck/robot-state.json'))
except Exception as e:
    print('state не получен:', e); raise SystemExit
print('orderId', d.get('orderId'), 'orderUpdateId', d.get('orderUpdateId'), 'driving', d.get('driving'))
print('lastNodeId', d.get('lastNodeId'), 'lastNodeSequenceId', d.get('lastNodeSequenceId'))
print('errors', [(e.get('errorLevel'), e.get('errorDescription')) for e in d.get('errors') or []])
print('actions', [(a.get('actionType'), a.get('actionStatus')) for a in d.get('actionStates') or []])
print('nodeStates', len(d.get('nodeStates') or []), 'edgeStates', len(d.get('edgeStates') or []))
pos=d.get('agvPosition') or {}
print('pos', pos.get('x'), pos.get('y'), pos.get('theta'), 'localized', pos.get('positionInitialized'))
PY
} > $OUT/summary.txt 2>&1

tar -czf /tmp/stuck.tgz -C $OUT .
ls -la /tmp/stuck.tgz
