#!/usr/bin/env python3
"""
Gate for the Linux focus hand-back + frontmost-guard (workspace-ynn9.4 / ynn9.7).

On a real Linux desktop the terminal is a separate X window; hiding/iconifying the
browser lets the WM reassign focus, so WireCopy re-activates the terminal window —
BUT only when the focus was on OUR stuff (browser/terminal). If a FOREIGN window
holds focus (the user switched away), WireCopy must respect it and NOT yank focus.

Two scenarios under Xvfb + openbox, with a stand-in terminal T (WINDOWID=T) and a
foreign distractor D:

  A. HAND-BACK FIRES: dock, then focus WireCopy's own browser window B (as if the
     user clicked into the docked page), then dismiss ('|'). Assert focus -> T
     (WireCopy moved focus from its browser back to the terminal).

  B. GUARD RESPECTS FOREIGN: dock, then focus the foreign distractor D, then dismiss.
     Assert focus STAYS on D (the frontmost-guard skipped the hand-back).

Needs: Xvfb, openbox, xdotool, ffmpeg, tmux, a Release build.
"""
import glob
import http.server
import os
import subprocess
import sys
import tempfile
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest  # noqa: E402

DISPLAY = ":98"
CHROME = glob.glob(os.path.expanduser("~/.cache/ms-playwright/chromium-*/chrome-linux/chrome"))


class Site(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        links = "".join(
            f'<div style="height:120px"><a href="/story-{i}">Headline number {i} about topic {i}</a></div>'
            for i in range(1, 31))
        html = f"<!DOCTYPE html><html><head><title>FHGazette</title></head><body><h1>FHGazette</h1>{links}</body></html>"
        data = html.encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *a):
        pass


def main():
    if not CHROME:
        print("SKIP: no chromium binary found for the stand-in windows")
        return 0
    chrome = CHROME[-1]
    for lk in glob.glob("/tmp/.X98-lock"):
        try:
            os.remove(lk)
        except OSError:
            pass

    xvfb = subprocess.Popen(["Xvfb", DISPLAY, "-screen", "0", "1600x900x24"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.5)
    env = {**os.environ, "DISPLAY": DISPLAY}
    ob = subprocess.Popen(["openbox"], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.0)

    def standin(tag, color, dd):
        return subprocess.Popen(
            [chrome, f"--user-data-dir={dd}", "--no-sandbox", "--disable-gpu", "--no-first-run",
             f"--app=data:text/html,<title>{tag}</title><body style='background:{color}'>"],
            env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def search(name):
        r = subprocess.run(["xdotool", "search", "--name", name], env=env, capture_output=True, text=True)
        ids = [x for x in r.stdout.split() if x.strip()]
        return ids[-1] if ids else None

    def activate(wid):
        subprocess.run(["xdotool", "windowactivate", "--sync", wid], env=env, capture_output=True)
        time.sleep(0.8)

    def active():
        return subprocess.run(["xdotool", "getactivewindow"], env=env, capture_output=True, text=True).stdout.strip()

    term_proc = standin("WC_TERMINAL_STANDIN", "#204020", tempfile.mkdtemp())
    dist_proc = standin("WC_DISTRACTOR", "#402020", tempfile.mkdtemp())
    time.sleep(6)
    T = search("WC_TERMINAL_STANDIN")
    D = search("WC_DISTRACTOR")
    print(f"stand-in terminal T={T}  distractor D={D}")
    if not T or not D:
        print("FAIL: could not create stand-in windows")
        return 1

    data_home = tempfile.mkdtemp(prefix="wirecopy-fh-")
    os.environ["DISPLAY"] = DISPLAY
    os.environ["XDG_DATA_HOME"] = data_home
    os.environ["Browser__Sidecar"] = "true"
    os.environ["WINDOWID"] = T
    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/fh-tmux"
    os.makedirs(os.environ["TMUX_TMPDIR"], exist_ok=True)
    subprocess.run(["tmux", "kill-server"], capture_output=True)

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Site)
    port = server.server_address[1]
    threading.Thread(target=server.serve_forever, daemon=True).start()

    failures = []
    try:
        with TermTest(url=f"http://127.0.0.1:{port}/", width=150, height=40) as t:
            t.wait_for("Headline number", timeout=90)
            t.wait_for("docked", timeout=90)
            time.sleep(3)

            # WireCopy's own browser window (the docked page titled "FHGazette").
            B = search("FHGazette")
            print(f"WireCopy browser window B={B}")
            if not B:
                failures.append("could not locate WireCopy's browser window")

            # --- Scenario A: our browser had focus -> hand-back fires, focus -> T ---
            if B:
                activate(B)
                print(f"[A] focused our browser: active={active()} (B={B})")
                t.send_keys("|")
                t.wait_until_gone("docked", timeout=20)
                time.sleep(2)
                a = active()
                print(f"[A] after dismiss: active={a} (want T={T})")
                if a != T:
                    failures.append(f"A: hand-back did not return focus to terminal (active={a}, want T={T})")
                # re-dock for scenario B
                t.send_keys("|")
                t.wait_for("docked", timeout=30)
                time.sleep(3)

            # --- Scenario B: a foreign window had focus -> guard skips, focus stays D ---
            activate(D)
            print(f"[B] focused foreign distractor: active={active()} (D={D})")
            t.send_keys("|")
            t.wait_until_gone("docked", timeout=20)
            time.sleep(2)
            a = active()
            print(f"[B] after dismiss: active={a} (want D={D} — guard respects foreign focus)")
            if a != D:
                failures.append(f"B: frontmost-guard did not respect foreign focus (active={a}, want D={D})")
    finally:
        server.shutdown()
        for p in (term_proc, dist_proc, ob, xvfb):
            try:
                p.terminate()
            except Exception:
                pass
        subprocess.run(["tmux", "kill-server"], capture_output=True)

    print()
    if failures:
        print("FOCUS HAND-BACK GATE FAILED:")
        for f in failures:
            print(f"  - {f}")
        return 1
    print("FOCUS HAND-BACK GATE PASSED — hand-back fires when our browser had focus; "
          "guard respects a foreign window")
    return 0


if __name__ == "__main__":
    sys.exit(main())
