#!/bin/bash
# Настройки автопереездов -> appsettings.Production.json (не входит в архив выкладки).
# JSON готовим обычным пользователем, под sudo только копирование.
set -x
S="sudo -S -p '' "
W=/opt/rms/vrp-workers

python3 - <<'PY'
import json, os

path = '/opt/rms/vrp-workers/appsettings.Production.json'
cfg = {}
if os.path.exists(path):
    for enc in ('utf-8-sig', 'utf-8'):
        try:
            cfg = json.load(open(path, encoding=enc))
            break
        except Exception:
            cfg = {}

routing = cfg.setdefault('Routing', {})
routing['Charging'] = {
    'Enabled': True,
    'MapId': '801',
    'LowerLimit': 20,
    'TargetLimit': 80,
    'WaitingMinutes': 1,
    'PollSeconds': 3,
    'RetryDelaySeconds': 5,
    'MaxAttempts': 20,
    'IdleNodeToleranceMeters': 0.5,
    'OccupiedRadiusMeters': 0.75,
    'ChargerId': '20373',
    'PublishChargingCompleted': True,
    'ChargingCompletedTopic': 'agv-charging-completed',
    'AutoStopCharging': False,
}
with open('/tmp/chg-prod.json', 'w', encoding='utf-8') as f:
    json.dump(cfg, f, ensure_ascii=False, indent=2)
print('готово /tmp/chg-prod.json')
PY

echo valex024771 | $S cp /tmp/chg-prod.json $W/appsettings.Production.json
echo valex024771 | $S chown root:root $W/appsettings.Production.json
echo "=== проверка ==="
python3 -c "
import json
for p in ('/opt/rms/vrp-workers/appsettings.json','/opt/rms/vrp-workers/appsettings.Production.json'):
    try:
        c=json.load(open(p,encoding='utf-8-sig'))
        print(p, '->', (c.get('Routing') or {}).get('Charging',{}).get('Enabled'))
    except Exception as e:
        print(p, 'ошибка', e)
"

echo valex024771 | $S systemctl restart vrp-workers
sleep 12
echo valex024771 | $S journalctl -u vrp-workers --since "-1 min" -o cat --no-pager 2>/dev/null \
  | grep -aiE 'Автопереезды' | tail -2

echo "=== ждём 50 с ==="
sleep 50
cat > /tmp/q.sql <<'SQL'
\pset pager off
select "OrderId","Status","TargetNodeId","CreatedAt" from "vrt_RobotOrder" order by "CreatedAt" desc limit 2;
select "SerialNumber","BatteryCharge","LastNodeId" from "Agvs";
SQL
echo valex024771 | $S -u postgres psql -d rms -f /tmp/q.sql
