// Wire Copy overlay terminal (workspace-iohy). Hosts the real xterm.js TUI and bridges it to the
// backend PTY child over ws://<backend>/ws/terminal?session=<id> — the exact same terminal pipeline
// as the legacy web shell, but the surface lives inside the user's own tab (as an extension page)
// instead of a dedicated localhost tab. The "web pane" is no longer a screencast: the real site is
// the tab's own top-level page, visible underneath this overlay.

const params = new URLSearchParams(location.search);
const sessionId = params.get("session") || (crypto.randomUUID ? crypto.randomUUID() : String(Math.random())).replace(/-/g, "");
const backend = (params.get("backend") || "ws://127.0.0.1:5099").replace(/^http/i, "ws").replace(/\/+$/, "");
const statusEl = document.getElementById("wc-status");

const term = new Terminal({
  fontFamily: 'ui-monospace, "DejaVu Sans Mono", monospace',
  fontSize: 16,
  cursorBlink: true,
  customGlyphs: true,
  theme: { background: "#000000" },
});
const fit = new FitAddon.FitAddon();
term.loadAddon(fit);
term.open(document.getElementById("term"));
fit.fit();
term.focus();

const enc = new TextEncoder();
const dec = new TextDecoder();
let socket = null;
let retry = 0;

function sendResize() {
  if (socket && socket.readyState === WebSocket.OPEN) {
    socket.send(JSON.stringify({ type: "resize", cols: term.cols, rows: term.rows }));
  }
}

function connect() {
  socket = new WebSocket(`${backend}/ws/terminal?session=${encodeURIComponent(sessionId)}`);
  socket.binaryType = "arraybuffer";
  socket.onopen = () => { retry = 0; statusEl.textContent = "live"; sendResize(); };
  socket.onmessage = (ev) => {
    if (typeof ev.data === "string") term.write(ev.data);
    // One persistent streaming decoder: the PTY chunks at arbitrary byte boundaries, so a multibyte
    // glyph can split across frames; { stream: true } buffers the tail instead of emitting U+FFFD.
    else term.write(dec.decode(new Uint8Array(ev.data), { stream: true }));
  };
  socket.onclose = () => {
    statusEl.textContent = "reconnecting…";
    retry = Math.min(retry + 1, 5);
    setTimeout(connect, 400 * retry);
  };
  socket.onerror = () => { try { socket.close(); } catch { /* ignore */ } };
}

term.onData((data) => {
  if (socket && socket.readyState === WebSocket.OPEN) socket.send(enc.encode(data));
});

// Keep the PTY's view of the grid in step with the overlay size. workspace-opb2: after a resize the
// xterm buffer is correct but on-screen pixels can be stale (partial repaint); debounce rapid resizes
// and force a full re-render of every row from the buffer once the resize settles.
let resizeTimer = null;
function refit() { fit.fit(); sendResize(); }
function scheduleRepaint() {
  refit();
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => {
    refit();
    try { term.refresh(0, term.rows - 1); } catch { /* renderer not ready */ }
  }, 100);
}
window.addEventListener("resize", scheduleRepaint);
window.addEventListener("focus", () => term.focus());

connect();
