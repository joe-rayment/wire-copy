#!/usr/bin/env python3
"""workspace-frpl.13 (B11) live gate: a finished, UNACKNOWLEDGED failed scheduled run
surfaces a launcher badge on next focus, and opening :schedules acknowledges it (badge
clears). A failed run is seeded directly into the ScheduledRuns table (fast, no TTS/GCS,
no slow create+run-now), then the real app + launcher render is exercised. Frames -> docs/qa."""
import sys, os, time, json, uuid, sqlite3
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest
import test_schedules_tui as g

DATA = g.DATA
DB = os.path.join(DATA, "wirecopy.db")
QA = os.path.join("/workspace/docs/qa", "workspace-frpl.13")
os.makedirs(QA, exist_ok=True)
RES = {"bead": "workspace-frpl.13", "assertions": []}
EF_DT = "2026-05-31 20:00:00.0000000"  # EF Core SQLite DateTime TEXT format


def chk(label, ok, frame=None):
    RES["assertions"].append({"label": label, "pass": bool(ok)})
    print(("PASS" if ok else "FAIL"), "—", label, flush=True)
    if not ok and frame:
        print("\n".join(frame.splitlines()[-10:]), flush=True)
    return ok


def seed_failed_run(name="Seeded Brief"):
    con = sqlite3.connect(DB)
    con.execute(
        "INSERT INTO ScheduledRuns (Id,RecipeId,RecipeName,OccurrenceKey,Status,StartedAtUtc,FinishedAtUtc,ItemCount,ErrorClass,ErrorMessage,AcknowledgedAtUtc) "
        "VALUES (?,?,?,?,?,?,?,?,?,?,NULL)",
        # EF Core SQLite binds Guid as UPPERCASE TEXT; seed it uppercase so the
        # acknowledge UPDATE's "WHERE Id =" matches (lowercase -> 0 rows affected).
        (str(uuid.uuid4()).upper(), str(uuid.uuid4()).upper(), name, "2026-05-31@07:00#seed", 4, EF_DT, EF_DT, 0,
         "NoContentResolved", "A required section contributed no articles"))
    con.commit(); con.close()


def goto_launcher(t):
    t.send_text(":"); time.sleep(0.6); t.send_text("home"); time.sleep(0.5); t.send_keys("Enter"); time.sleep(2.0)


def main():
    all_ok = True
    if not os.path.exists(DB):
        print("FATAL: db missing (launch the app once first)"); return 1
    # Deterministic state: no recipes (so the in-process scheduler can't auto-add
    # runs on startup and pollute the badge), and exactly one seeded failed run.
    sj = os.path.join(DATA, "schedules.json")
    if os.path.exists(sj):
        os.remove(sj)
    con = sqlite3.connect(DB); con.execute("DELETE FROM ScheduledRuns"); con.commit(); con.close()
    seed_failed_run()

    g.clear_browser_locks()
    with TermTest(url="https://text.npr.org", width=120, height=40) as t:
        if not g.wait_content(t):
            print("FATAL load"); return 1
        goto_launcher(t)
        s = t.capture()
        with open(os.path.join(QA, "01_launcher_badge.txt"), "w") as f:
            f.write(s)
        all_ok &= chk("launcher badge shows the failed run", "⚠" in s and "Seeded Brief" in s and "failed" in s.lower(), s)

        # Open :schedules -> acknowledges all finished runs -> badge clears.
        for _ in range(4):
            g.open_schedules(t)
            if "Schedules" in t.capture():
                break
            time.sleep(1)
        time.sleep(1.5)
        t.send_keys("Escape"); time.sleep(0.6)
        goto_launcher(t)
        s = t.capture()
        with open(os.path.join(QA, "02_launcher_cleared.txt"), "w") as f:
            f.write(s)
        all_ok &= chk("badge clears after opening :schedules (acknowledged)", "⚠" not in s and "need attention" not in s.lower(), s)

    # verify the DB row was actually acknowledged (the clear is real, not cosmetic)
    con = sqlite3.connect(DB)
    acked = con.execute("SELECT AcknowledgedAtUtc FROM ScheduledRuns WHERE OccurrenceKey='2026-05-31@07:00#seed'").fetchone()
    con.close()
    all_ok &= chk("the failed run row is acknowledged in the DB", acked is not None and acked[0] is not None)

    RES["result"] = "PASS" if all_ok else "FAIL"
    json.dump(RES, open(os.path.join(QA, "result.json"), "w"), indent=2)
    print("===", RES["result"], "===", flush=True)
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
