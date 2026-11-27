import json, os

base = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def load(name):
    with open(os.path.join(base, name), encoding="utf-8") as f:
        return json.load(f)


ours = load("our-same-route-order.json")
ref = load("ref-pick-drop-wo-2-7-wo-1-6.json")


def nodes(o):
    return {n["nodeId"]: n for n in o["nodes"]}


def acts(n):
    return [
        (a.get("actionType"), a.get("blockingType")) for a in n.get("actions") or []
    ]


print(
    "ROUTE",
    [n["nodeId"] for n in ref["nodes"]],
    "vs",
    [n["nodeId"] for n in ours["nodes"]],
)
print()
print("%-10s | %-34s | %-34s" % ("node", "REF (old AGV)", "OURS"))
print("-" * 88)

for node_id in [n["nodeId"] for n in ref["nodes"]]:
    r = nodes(ref).get(node_id)
    o = nodes(ours).get(node_id)
    if r is None or o is None:
        print(
            "%-10s | %-34s | %-34s"
            % (
                node_id,
                "missing" if r is None else "-",
                "missing" if o is None else "-",
            )
        )
        continue
    rp, op = r["nodePosition"], o["nodePosition"]
    rth = "theta=%s" % rp.get("theta") if "theta" in rp else "theta=absent"
    oth = "theta=%s" % op.get("theta") if "theta" in op else "theta=absent"
    rdev = "dev=%s/%s" % (rp.get("allowedDeviationXY"), rp.get("allowedDeviationTheta"))
    odev = "dev=%s/%s" % (op.get("allowedDeviationXY"), op.get("allowedDeviationTheta"))
    print("%-10s | %-34s | %-34s" % (node_id, rth + " " + rdev, oth + " " + odev))
    print(
        "%-10s | %-34s | %-34s"
        % (
            "",
            "xy=%s,%s" % (round(rp["x"], 4), round(rp["y"], 4)),
            "xy=%s,%s" % (round(op["x"], 4), round(op["y"], 4)),
        )
    )
    if acts(r) or acts(o):
        print(
            "%-10s | %-34s | %-34s"
            % ("", "actions=" + str(acts(r)), "actions=" + str(acts(o)))
        )

print()
print("EDGES")
print(
    "%-48s | %-8s | %-8s | %s" % ("edgeId", "ref.ori", "our.ori", "extra keys in ours")
)
seen = set()
for e in ref["edges"] + ours["edges"]:
    eid = e["edgeId"]
    if eid in seen:
        continue
    seen.add(eid)
    r = next((x for x in ref["edges"] if x["edgeId"] == eid), None)
    o = next((x for x in ours["edges"] if x["edgeId"] == eid), None)
    extra = sorted(set(o.keys()) - set(r.keys())) if (r and o) else []
    print(
        "%-48s | %-8s | %-8s | %s"
        % (
            eid,
            r["orientation"] if r else "absent",
            o["orientation"] if o else "absent",
            ",".join(extra),
        )
    )

print()
print("ref edge control points:")
for e in ref["edges"]:
    cps = " -> ".join(
        "(%s,%s)" % (c["x"], c["y"]) for c in e["trajectory"]["controlPoints"]
    )
    print("  %-40s %s" % (e["edgeId"], cps))
print("our edge control points:")
for e in ours["edges"]:
    cps = " -> ".join(
        "(%s,%s)" % (c["x"], c["y"]) for c in e["trajectory"]["controlPoints"]
    )
    print("  %-40s %s" % (e["edgeId"], cps))
