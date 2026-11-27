#!/bin/bash
S="sudo -S -p '' "

echo "== симулятор: всё кроме AuthenticationRequiredError, последние 400 строк =="
echo valex024771 | $S journalctl -u robots.simulator -o short-iso --no-pager 2>/dev/null \
  | grep -v AuthenticationRequiredError | tail -n 400 > /tmp/sim-clean.txt
wc -l /tmp/sim-clean.txt
echo "--- последние 40 ---"
tail -n 40 /tmp/sim-clean.txt
echo "--- окно 24.11 20:20-20:40 ---"
grep -E '2026-11-24T20:(2|3)' /tmp/sim-clean.txt | head -40

echo
echo "== активность топиков APR за 12 секунд =="
timeout 12 mosquitto_sub -h 127.0.0.1 -p 1883 -u rcs -P ntls1234 -t 'APR/#' -v 2>&1 | cut -c1-140 > /tmp/apr-topics.txt
wc -l /tmp/apr-topics.txt
head -n 20 /tmp/apr-topics.txt

echo
echo "== процесс симулятора =="
ps -eo pid,etime,stat,cmd | grep -i RobotsSimulator | grep -v grep
