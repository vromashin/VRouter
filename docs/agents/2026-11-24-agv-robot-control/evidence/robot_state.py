import json, subprocess

raw = subprocess.run(
    [
        "timeout",
        "15",
        "mosquitto_sub",
        "-h",
        "127.0.0.1",
        "-p",
        "1883",
        "-u",
        "rcs",
        "-P",
        "ntls1234",
        "-t",
        "APR/v2/Tuskrobots/98001/state",
        "-C",
        "1",
    ],
    capture_output=True,
    text=True,
).stdout
d = json.loads(raw)
print(
    "orderId",
    d.get("orderId"),
    "upd",
    d.get("orderUpdateId"),
    "driving",
    d.get("driving"),
)
print("lastNodeId", d.get("lastNodeId"), "lastSeq", d.get("lastNodeSequenceId"))
print("errors", [e.get("errorDescription") for e in d.get("errors") or []])
print(
    "actions",
    [(a.get("actionType"), a.get("actionStatus")) for a in d.get("actionStates") or []],
)
pos = d.get("agvPosition") or {}
print(
    "pos",
    round(pos.get("x", 0), 4),
    round(pos.get("y", 0), 4),
    round(pos.get("theta", 0), 4),
)
