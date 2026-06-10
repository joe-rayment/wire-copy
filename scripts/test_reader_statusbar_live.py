#!/usr/bin/env python3
"""workspace-w7oe repro — does a transient status message render in reader view?"""
import os, re, shutil, subprocess, sys, tempfile, time

sys.path.insert(0, "/workspace/scripts")
from termtest import TermTest

DISPLAY = ":96"

def status_rows(screen):
    return "\n".join(screen.rstrip("\n").split("\n")[-3:])

def main():
    data_home = tempfile.mkdtemp(prefix="wirecopy-w7oe-")
    os.environ["XDG_DATA_HOME"] = data_home
    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/w7oe-tmux"
    os.makedirs(os.environ["TMUX_TMPDIR"], exist_ok=True)
    subprocess.run(["tmux", "kill-server"], capture_output=True)

    lock = f"/tmp/.X{DISPLAY.lstrip(':')}-lock"
    if os.path.exists(lock):
        try:
            with open(lock) as fh:
                os.kill(int(fh.read().strip()), 0)
        except (OSError, ValueError):
            os.remove(lock)
    xvfb = subprocess.Popen(["Xvfb", DISPLAY, "-screen", "0", "1600x900x24"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    os.environ["DISPLAY"] = DISPLAY

    failures = []
    try:
        with TermTest(url="http://127.0.0.1:8642/disaster/fighting-the-flames-with-dynamite.html",
                      width=100, height=35) as t:
            try:
                t.wait_for("ReaderView", timeout=120)
            except Exception:
                print("!!! never reached ReaderView; pane:")
                print(t.capture())
                raise
            time.sleep(2)

            screen = t.capture()
            print("=== ReaderView status rows BEFORE keypress ===")
            print(status_rows(screen))

            t.send_keys(">")
            time.sleep(0.7)
            screen = t.capture()
            print("=== status rows after '>' (expect 'NNN WPM') ===")
            print(status_rows(screen))
            if "WPM" not in screen:
                failures.append("'>' WPM status message did NOT render in reader view")
            else:
                print("--- WPM message visible")

            time.sleep(3.5)
            t.send_keys("y")
            time.sleep(0.7)
            screen = t.capture()
            print("=== status rows after 'y' ===")
            print(status_rows(screen))
            row = status_rows(screen).lower()
            if "browser" not in row and "nothing new" not in row and "opening" not in row:
                failures.append("'y' adoption status message did NOT render in reader view")
            else:
                print("--- adoption message visible")
    finally:
        subprocess.run(["tmux", "kill-server"], capture_output=True)
        xvfb.terminate()
        shutil.rmtree(data_home, ignore_errors=True)

    if failures:
        print("\nFAILURES:")
        for f in failures:
            print(f"  ✗ {f}")
        sys.exit(1)
    print("✓ status messages render in reader view")

if __name__ == "__main__":
    main()
