#!/bin/bash
S="sudo -S -p '' "

echo "=== сервисы ==="
systemctl is-active robots.simulator vrp-api vrp-workers
echo "=== порты симулятора ==="
echo valex024771 | $S ss -ltnp 2>/dev/null | grep 5020

echo "=== поток state за 15 с ==="
timeout 15 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 -t "APR/v2/Tuskrobots/98001/state" -C 1 > /tmp/st.json 2>/tmp/st.err
echo "байт: $(wc -c < /tmp/st.json) err: $(cat /tmp/st.err)"
python3 - <<'PY'
import json
try:
    d=json.load(open('/tmp/st.json'))
    b=d.get('batteryState') or {}
    print('lastNode', d.get('lastNodeId'), 'driving', d.get('driving'),
          'battery', round(b.get('batteryCharge',0),2), 'charging', b.get('charging'))
    print('actions', [(a.get('actionType'), a.get('actionStatus')) for a in d.get('actionStates') or []])
    print('errors', [e.get('errorDescription') for e in d.get('errors') or []])
except Exception as e:
    print('state не прочитан:', e)
PY

echo "=== kafka agv-charging-completed с начала ==="
echo valex024771 | $S timeout 25 rpk topic consume agv-charging-completed -o start -f '%o | %k | %v\n' 2>&1 | head -5

echo "=== БД ==="
cat > /tmp/q.sql <<'SQL'
\pset pager off
select "SerialNumber","BatteryCharge","IsCharging","LastNodeId","CurrentOrderId" from "Agvs";
SQL
echo valex024771 | $S -u postgres psql -d rms -f /tmp/q.sql

echo "=== журнал симулятора, последние 15 строк без auth ==="
echo valex024771 | $S journalctl -u robots.simulator --since "-10 min" -o cat --no-pager 2>/dev/null | grep -v AuthenticationRequiredError | tail -15
