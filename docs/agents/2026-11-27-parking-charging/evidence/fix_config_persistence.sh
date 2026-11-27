#!/bin/bash
# Настройки автопереездов кладем в appsettings.Production.json: он не входит в архив выкладки
# и не перезаписывается при обновлении.
set -x
S="sudo -S -p '' "

echo "=== что сейчас в appsettings.json ==="
python3 - <<'PY'
import json
for p in ('/opt/rms/vrp-workers/appsettings.json', '/opt/rms/vrp-workers/appsettings.Production.json'):
    try:
        cfg = json.load(open(p, encoding='utf-8'))
        ch = (cfg.get('Routing') or {}).get('Charging') or {}
        print(p, '-> Enabled =', ch.get('Enabled'))
    except FileNotFoundError:
        print(p, '-> нет файла')
PY

echo "=== пишем Production ==="
echo valex024771 | $S python3 - <<'PY'
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
with open(path, 'w', encoding='utf-8') as f:
    json.dump(cfg, f, ensure_ascii=False, indent=2)
print('written', path)
PY

echo "=== перезапуск и проверка ==="
echo valex024771 | $S systemctl restart vrp-workers
sleep 15
echo valex024771 | $S journalctl -u vrp-workers --since "-1 min" -o cat --no-pager 2>/dev/null \
  | grep -aiE 'Автопереезды включены|Enabled' | tail -3

echo "=== ждём реакцию 45 с ==="
sleep 45
cat > /tmp/q.sql <<'SQL'
\pset pager off
select "OrderId","Status","TargetNodeId","CreatedAt" from "vrt_RobotOrder" order by "CreatedAt" desc limit 2;
select "SerialNumber","BatteryCharge","LastNodeId" from "Agvs";
SQL
echo valex024771 | $S -u postgres psql -d rms -f /tmp/q.sql
