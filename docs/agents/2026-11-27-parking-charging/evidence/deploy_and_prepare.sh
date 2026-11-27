#!/bin/bash
# Выкладка сборки с автопереездами и подготовка стенда к живому прогону.
set -x
S="sudo -S -p '' "
B=$(date +%s)

echo "=== бэкап и выкладка ==="
echo valex024771 | $S tar -czf /tmp/bk-workers-$B.tgz -C /opt/rms/vrp-workers .
echo valex024771 | $S tar -czf /tmp/bk-api-$B.tgz -C /opt/rms/vrp-api .
echo valex024771 | $S systemctl stop vrp-api vrp-workers
sleep 2
echo valex024771 | $S tar -xzf /tmp/workers.tgz -C /opt/rms/vrp-workers
echo valex024771 | $S tar -xzf /tmp/api.tgz -C /opt/rms/vrp-api

echo "=== правим секцию Charging в конфигах стенда ==="
echo valex024771 | $S python3 - <<'PY'
import json

def patch(path):
    with open(path, encoding='utf-8') as f:
        cfg = json.load(f)
    routing = cfg.setdefault('Routing', {})
    ch = routing.setdefault('Charging', {})
    ch.update({
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
        'PublishChargingCompleted': True,
        'ChargingCompletedTopic': 'agv-charging-completed',
        'AutoStopCharging': False,
    })
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)
    print('patched', path, ch)

patch('/opt/rms/vrp-api/appsettings.json')
patch('/opt/rms/vrp-workers/appsettings.json')
PY

echo "=== запуск ==="
echo valex024771 | $S systemctl start vrp-api vrp-workers
sleep 20
echo "api=$(systemctl is-active vrp-api) workers=$(systemctl is-active vrp-workers) sim=$(systemctl is-active robots.simulator)"
echo "=== порт api ==="
echo valex024771 | $S ss -ltnp 2>/dev/null | grep -E 'vrp|50[0-9][0-9]' | head -5
echo "=== журнал старта воркера ==="
echo valex024771 | $S journalctl -u vrp-workers --since "-1 min" -o cat --no-pager 2>/dev/null | grep -aiE 'Charging|переезд|Relocation|ошибка|error|fail' | tail -10
