import json, sys, os

base = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def load(name):
    with open(os.path.join(base, name), encoding="utf-8") as f:
        return json.load(f)


def shape(o, label):
    print("=" * 70)
    print(
        label,
        "| orderId=",
        o.get("orderId"),
        "| orderUpdateId=",
        o.get("orderUpdateId"),
    )
    print("top keys:", sorted(o.keys()))
    print("-- nodes --")
    for n in o.get("nodes", []):
        np = n.get("nodePosition", {})
        acts = ",".join(
            a.get("actionType", "?") + "/" + str(a.get("blockingType"))
            for a in n.get("actions") or []
        )
        print(
            f"  seq={n.get('sequenceId')} id={n.get('nodeId'):10s} "
            f"theta={'YES' if 'theta' in np else 'no':3s} "
            f"xy={np.get('x')},{np.get('y')} dev={np.get('allowedDeviationXY')}/{np.get('allowedDeviationTheta')} "
            f"acts=[{acts}] nodeKeys={sorted(n.keys())}"
        )
    print("-- edges --")
    for e in o.get("edges", []):
        cps = e.get("trajectory", {}).get("controlPoints", [])
        cps = " ".join(f"({c.get('x')},{c.get('y')})" for c in cps)
        extra = sorted(
            set(e.keys())
            - {
                "edgeId",
                "sequenceId",
                "startNodeId",
                "endNodeId",
                "orientation",
                "orientationType",
                "released",
                "trajectory",
                "actions",
            }
        )
        print(
            f"  seq={e.get('sequenceId')} id={e.get('edgeId')} orient={e.get('orientation')} type={e.get('orientationType')} "
            f"released={e.get('released')} cp={cps} extra={extra} acts={e.get('actions')}"
        )
    print("-- actions detail --")
    for n in o.get("nodes", []):
        for a in n.get("actions") or []:
            print(f"  node={n.get('nodeId')} action keys={sorted(a.keys())}")
            for p in a.get("actionParameters", []):
                v = p.get("value")
                print(f"    {p.get('key')} = {v!r} ({type(v).__name__})")


ref = load("ref-pick-drop-wo-2-7-wo-1-6.json")
shape(ref, "REF (old AGV, pick wo-2-7 -> drop wo-1-6)")

for name in ["our-order-wo-2-7.json", "old-order-wo-2-7.json"]:
    if os.path.exists(os.path.join(base, name)):
        shape(load(name), name)
