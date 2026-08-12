"""Sample GET /api/bots/{id}/target on a tick and write one CSV row per sample.

The target view exists so a whole fight can be MEASURED rather than watched (operator
2026-08-12). This is the measuring end of it: point it at a bot, leave it running across an
instance run, and the result answers questions a human staring at the watch page cannot —
what fraction of the fight the target spent outside a 45 degree cast arc, whether the two
copies of a scenario clone ever disagreed about its position, and how the target's HP moved
against ours.

    python tools/target_sample.py ClericFresh --seconds 900 --out cleric.csv

Reads the bearer token from BOT_API_TOKEN and the host from BOT_HOST (default
https://bots.ikaron.uk) — neither is baked in, this file is committed.
"""
import argparse, csv, json, os, sys, time, urllib.request

FIELDS = ["ts", "map", "selfX", "selfY", "selfHp", "selfMaxHp", "handle", "asserted",
          "kind", "name", "level", "mobId", "hp", "maxHp", "dist", "facingDeg",
          "bearingDeg", "angleOffDeg", "posDisagreeU", "scenarioFightable", "aggro",
          "inView", "heldSeconds"]


def sample(host, token, bot):
    req = urllib.request.Request(f"{host}/api/bots/{bot}/target",
                                 headers={"Authorization": f"Bearer {token}"} if token else {})
    d = json.load(urllib.request.urlopen(req, timeout=15))
    t, s = d["target"], d.get("self") or {}
    return {"ts": d["atUtc"], "map": d.get("map"),
            "selfX": s.get("x"), "selfY": s.get("y"), "selfHp": s.get("hp"), "selfMaxHp": s.get("maxHp"),
            **{k: t.get(k) for k in FIELDS if k in t}}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("bot")
    ap.add_argument("--seconds", type=float, default=600)
    ap.add_argument("--every", type=float, default=1.0)
    ap.add_argument("--out", default=None)
    a = ap.parse_args()
    host = os.environ.get("BOT_HOST", "https://bots.ikaron.uk").rstrip("/")
    token = os.environ.get("BOT_API_TOKEN", "")
    out = open(a.out, "w", newline="", encoding="utf-8") if a.out else sys.stdout
    w = csv.DictWriter(out, fieldnames=FIELDS, extrasaction="ignore")
    w.writeheader()
    deadline = time.time() + a.seconds
    while time.time() < deadline:
        try:
            w.writerow(sample(host, token, a.bot))
            out.flush()
        except Exception as e:                      # a restarting pod must not end the run
            print(f"# {time.strftime('%H:%M:%S')} {type(e).__name__}: {e}", file=sys.stderr)
        time.sleep(a.every)


if __name__ == "__main__":
    main()
