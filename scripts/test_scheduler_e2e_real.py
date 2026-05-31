#!/usr/bin/env python3
"""workspace-frpl.17 (B13) — LIVE end-to-end gate for the scheduled-run path.

(1) Creates a recipe via the real ':schedules' TUI over a bookmarked ALREADY-configured
    free site (text.npr.org), SingleTopStory (one article -> minimal TTS cost).
(2) Runs it to completion through the SAME gate + RecipeRunPipeline + orchestrator +
    real GCS publish via the non-interactive `run-recipe` verb (the ~minutes-long
    generation as a reliable subprocess, not a long interactive tmux session).
(3) Asserts a real published episode: feed.xml HEADs 200, the first <enclosure> HEADs
    200, and ffprobe confirms the local M4B has non-zero duration/size.

GATED: requires a configured GCS bucket + OpenAI key (real TTS + GCS cost). No
example.com, no internal-handoff shortcut. Evidence -> docs/qa/workspace-frpl.17/."""
import sys, os, time, json, sqlite3, shutil, subprocess, glob
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest
import test_schedules_tui as g
from test_podcast_e2e_real import verify_feed, verify_first_audio_in_feed, verify_audio, get_bucket_name

DATA = g.DATA
DB = os.path.join(DATA, "wirecopy.db")
CFG = os.path.join(DATA, "hierarchy", "text.npr.org.json")
DLL = os.path.join("/workspace", "src/WireCopy.API/bin/Debug/net10.0/WireCopy.API.dll")
DOTNET = os.path.join("/workspace", "dotnet")
QA = os.path.join("/workspace/docs/qa", "workspace-frpl.17")
os.makedirs(QA, exist_ok=True)
RES = {"bead": "workspace-frpl.17", "assertions": []}


def chk(label, ok, detail=""):
    RES["assertions"].append({"label": label, "pass": bool(ok), "detail": str(detail)[:300]})
    print(("PASS" if ok else "FAIL"), "—", label, ("" if ok else f"  [{detail}]"), flush=True)
    return bool(ok)


def create_recipe(t):
    for _ in range(4):
        g.open_schedules(t)
        if "Schedules" in t.capture():
            break
        time.sleep(1)
    t.send_keys("a"); time.sleep(0.8)
    g.type_field(t, "E2E Brief")
    g.select_option(t, "Add a source"); t.send_keys("Enter"); time.sleep(0.8)
    g.select_option(t, "npr.org"); t.send_keys("Enter"); time.sleep(0.8)
    g.select_option(t, "Lead story"); t.send_keys("Enter"); time.sleep(0.6)
    g.select_option(t, "Just the top story"); t.send_keys("Enter"); time.sleep(0.6)
    g.select_option(t, "Required"); t.send_keys("Enter"); time.sleep(0.8)
    g.select_option(t, "Done"); t.send_keys("Enter"); time.sleep(0.8)
    g.select_option(t, "Every day"); t.send_keys("Enter"); time.sleep(0.6)
    g.type_field(t, ""); g.type_field(t, ""); time.sleep(1.0)


def main():
    all_ok = True
    if not get_bucket_name():
        print("SKIP: no GCS bucket configured (gated lane)"); return 1
    if not os.path.exists(CFG):
        for b in (CFG + ".gatebak", CFG + ".b11bak", CFG + ".keep"):
            if os.path.exists(b):
                shutil.copy(b, CFG); break
    if not os.path.exists(CFG):
        print("FATAL: text.npr.org config missing"); return 1

    if os.path.exists(os.path.join(DATA, "schedules.json")):
        os.remove(os.path.join(DATA, "schedules.json"))
    con = sqlite3.connect(DB); con.execute("DELETE FROM ScheduledRuns"); con.commit(); con.close()
    with open(os.path.join(DATA, "bookmarks.json"), "w") as f:
        json.dump({"version": 1, "bookmarks": [{"name": "NPR Text", "url": "https://text.npr.org"}]}, f)

    # (1) Create the recipe via the real TUI (short session).
    g.clear_browser_locks()
    with TermTest(url="https://text.npr.org", width=120, height=40) as t:
        if not g.wait_content(t):
            print("FATAL load"); return 1
        create_recipe(t)
        s = t.capture()
        with open(os.path.join(QA, "01_recipe_created.txt"), "w") as f:
            f.write(s)
        all_ok &= chk("recipe created via :schedules over the configured site", "E2E Brief" in s, s.splitlines()[-2:])

    if not os.path.exists(os.path.join(DATA, "schedules.json")):
        return chk("recipe persisted to schedules.json", False, "no schedules.json after create") or 1

    # (2) Run it to completion via the non-interactive verb (real TTS + GCS publish).
    # Bridge the GCS bucket + SA into IConfiguration via env vars (GcsConfiguration binds
    # the "Gcs" section; GcsStorageClient reads _config.BucketName / ServiceAccountKeyPath).
    g.clear_browser_locks()
    env = dict(os.environ)
    env["Gcs__BucketName"] = get_bucket_name()
    sa = glob.glob("/workspace/creds/*.json")
    if sa:
        env["Gcs__ServiceAccountKeyPath"] = sa[0]
    print("[e2e] running the recipe (real generation, may take minutes)…", flush=True)
    out = subprocess.run([DOTNET, DLL, "run-recipe", "E2E Brief"], cwd="/workspace",
                         capture_output=True, text=True, timeout=540, env=env)
    run = None
    for line in out.stdout.splitlines():
        if line.startswith("RUN_RESULT:"):
            run = json.loads(line[len("RUN_RESULT:"):])
    print("[e2e] run result:", run, flush=True)
    json.dump(run or {"stdout_tail": out.stdout[-500:], "stderr_tail": out.stderr[-500:]},
              open(os.path.join(QA, "02_run_result.json"), "w"), indent=2)
    all_ok &= chk("recipe ran to a successful/partial terminal status", run and run.get("status") in ("Completed", "PartialSuccess"), run)

    feed_url = (run or {}).get("feedUrl")
    local_path = (run or {}).get("localPath")

    # (3) Verify the real published artifacts.
    feed_ok, feed_info = verify_feed(feed_url) if feed_url else (False, {"error": "no feed url on the run"})
    all_ok &= chk("feed.xml HEAD returns 200", feed_ok and feed_info.get("status") == 200, feed_info)
    enc_ok, enc_info = verify_first_audio_in_feed(feed_url) if feed_url else (False, {"error": "no feed url"})
    all_ok &= chk("first enclosure HEADs 200", enc_ok and enc_info.get("status") == 200, enc_info)
    aud_ok, aud_info = verify_audio(local_path) if local_path else (False, {"error": "no local path"})
    all_ok &= chk("local M4B has non-zero duration/size (ffprobe)", aud_ok, aud_info)

    RES.update({"feed_url": feed_url, "local_path": local_path, "result": "PASS" if all_ok else "FAIL"})
    json.dump(RES, open(os.path.join(QA, "result.json"), "w"), indent=2)
    print("===", RES["result"], "===", flush=True)
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
