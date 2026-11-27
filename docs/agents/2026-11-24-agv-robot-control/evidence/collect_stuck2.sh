#!/bin/bash
S="sudo -S -p '' "
OUT=/tmp/stuck2
mkdir -p $OUT; rm -f $OUT/*

# 1. Состояние робота: ждём до 60 секунд
timeout 60 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 \
  -t "APR/v2/Tuskrobots/98001/state" -C 1 > $OUT/robot-state.json 2>$OUT/robot-state.err

# 2. БД через файл со SQL, иначе heredoc съедает пароль sudo
cat > $OUT/q.sql <<'SQL'
\pset pager off
select "OrderId","Status","OrderUpdateId","TargetNodeId","AgvSerialNumber","CreatedAt","SentAt","AcceptedAt","CompletedAt","FailureReason"
from "vrt_RobotOrder" order by "CreatedAt" desc limit 6;
select "OrderId","SequenceId","NodeId","ActionCount","Reached","Released"
from "vrt_RobotOrderNode"
where "OrderId" in (select "OrderId" from "vrt_RobotOrder" order by "CreatedAt" desc limit 2)
order by "OrderId","SequenceId";
select "SerialNumber","HasLoad","CurrentOrderId","LastNodeId","PositionX","PositionY","Theta" from "Agvs";
SQL
echo valex024771 | $S -u postgres psql -d rms -f $OUT/q.sql > $OUT/db-orders.txt 2>&1

# 3. Журнал воркеров: только значимые строки, без SQL
echo valex024771 | $S journalctl -u vrp-workers --since "-90 min" --no-pager -o cat 2>/dev/null \
  | grep -aE 'Заказ|Задача|Публикация|плеч|провал|ошибка|Error|error|warn|Warn|UPDATE_NOT_ACCEPTED|NO_ACCEPT|LEG_|initPosition|резерв|трафик' \
  | tail -400 > $OUT/workers.log
echo valex024771 | $S journalctl -u vrp-workers --since "-90 min" --no-pager -o cat 2>/dev/null | tail -300 > $OUT/workers-tail.log

# 4. События задач
echo valex024771 | $S timeout 40 rpk topic consume tasks-events -o end -n 15 -f '%o %v' > $OUT/tasks-events.log 2>$OUT/tasks-events.err

# 5. Состояние симулятора и статус юнитов
echo valex024771 | $S cat /opt/rms/ntls.simulator.robotssimulator/RobotsSimulatorStates.ini > $OUT/simulator-state.ini 2>&1
{ systemctl is-active robots.simulator vrp-api vrp-workers; date; } > $OUT/units.txt 2>&1

# 6. Сводка
{
  echo "== когда снято =="; date
  echo; echo "== юниты =="; cat $OUT/units.txt
  echo; echo "== робот =="
  if [ -s $OUT/robot-state.json ]; then python3 -c "
import json;d=json.load(open('$OUT/robot-state.json'))
print('orderId',d.get('orderId'),'orderUpdateId',d.get('orderUpdateId'),'driving',d.get('driving'))
print('lastNodeId',d.get('lastNodeId'),'lastNodeSequenceId',d.get('lastNodeSequenceId'))
print('errors',[(e.get('errorLevel'),e.get('errorDescription')) for e in d.get('errors') or []])
print('actions',[(a.get('actionType'),a.get('actionStatus')) for a in d.get('actionStates') or []])
print('nodeStates',len(d.get('nodeStates') or []),'edgeStates',len(d.get('edgeStates') or []))
p=d.get('agvPosition') or {}
print('pos',p.get('x'),p.get('y'),p.get('theta'),'init',p.get('positionInitialized'))
"; else echo "state не пришёл за 60 с"; cat $OUT/robot-state.err; fi
  echo; echo "== файлы =="; ls -la $OUT
} > $OUT/summary.txt 2>&1

tar -czf /tmp/stuck2.tgz -C $OUT .
ls -la /tmp/stuck2.tgz
