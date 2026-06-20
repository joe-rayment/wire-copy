// Content script: injects a docked top-layer overlay (the WireCopy TUI surface) into the user's own
// tab, captures the page's RENDERED DOM, sends it to the backend extractor, and renders the story
// list. Clicking a link spotlights it on the page UNDERNEATH (scroll + highlight). One tab, one
// window — the real site is the tab's top-level page; the overlay sits on top and drives it.
(function () {
  if (window.__wcSpikeInjected) return;
  window.__wcSpikeInjected = true;

  const PANEL_W = 460;
  const css = (el, s) => Object.assign(el.style, s);

  const host = document.createElement("div");
  host.id = "wc-spike-host";
  css(host, {
    position: "fixed", top: "0", left: "0", width: PANEL_W + "px", height: "100vh",
    zIndex: "2147483647", background: "#0b0b0e", color: "#bdf7c0",
    font: '13px ui-monospace, "DejaVu Sans Mono", monospace', borderRight: "2px solid #1f6f2a",
    boxShadow: "0 0 24px #000", display: "flex", flexDirection: "column", boxSizing: "border-box",
  });

  const hdr = document.createElement("div");
  css(hdr, { padding: "8px", borderBottom: "1px solid #1f6f2a", color: "#ff5fa2", fontWeight: "bold" });
  hdr.textContent = "Wire Copy — spike (host-browser renderer)";

  const row = document.createElement("div");
  css(row, { display: "flex", gap: "6px", padding: "8px" });
  const input = document.createElement("input");
  input.id = "wc-spike-url";
  input.value = location.href;
  css(input, { flex: "1", background: "#000", color: "#bdf7c0", border: "1px solid #333", padding: "4px", minWidth: "0" });
  const go = document.createElement("button");
  go.textContent = "Go";
  css(go, { background: "#114d22", color: "#bdf7c0", border: "1px solid #2a2", cursor: "pointer" });
  go.onclick = () => {
    let u = input.value.trim();
    if (!/^https?:\/\//i.test(u)) u = "https://" + u;
    chrome.runtime.sendMessage({ type: "navigate", url: u });
  };
  row.append(input, go);

  const status = document.createElement("div");
  status.id = "wc-spike-status";
  css(status, { padding: "4px 8px", color: "#7fd88a", fontSize: "12px" });
  status.textContent = "extracting…";

  const list = document.createElement("div");
  list.id = "wc-spike-list";
  css(list, { flex: "1", overflow: "auto", padding: "4px 8px" });

  host.append(hdr, row, status, list);

  function mount() {
    const root = document.body || document.documentElement;
    root.appendChild(host);
  }

  function spotlight(url) {
    try {
      const a = [...document.querySelectorAll("a[href]")].find(
        (x) => x.href === url || x.getAttribute("href") === url,
      );
      if (a) {
        a.scrollIntoView({ behavior: "smooth", block: "center" });
        const prev = a.style.outline;
        a.style.outline = "3px solid #ff5fa2";
        a.style.outlineOffset = "2px";
        setTimeout(() => { a.style.outline = prev; }, 1800);
      }
    } catch (e) { /* ignore */ }
  }

  function render(j) {
    status.textContent = (j.count || 0) + " links extracted by backend LinkExtractor";
    list.textContent = "";
    (j.links || []).slice(0, 200).forEach((l, i) => {
      const item = document.createElement("div");
      item.className = "wc-spike-item";
      css(item, { padding: "5px 0", borderBottom: "1px solid #122", cursor: "pointer" });
      const t = document.createElement("div");
      t.textContent = i + 1 + ". " + (l.text || "(no text)");
      css(t, { color: "#bdf7c0" });
      const u = document.createElement("div");
      u.textContent = l.url;
      css(u, { color: "#4aa874", fontSize: "11px", whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" });
      item.append(t, u);
      item.onclick = () => spotlight(l.url);
      list.appendChild(item);
    });
  }

  function extract() {
    status.textContent = "extracting (sending rendered DOM to backend)…";
    const html = document.documentElement.outerHTML;
    chrome.runtime.sendMessage({ type: "extract", url: location.href, html }, (resp) => {
      if (chrome.runtime.lastError) { status.textContent = "bridge error: " + chrome.runtime.lastError.message; return; }
      if (!resp) { status.textContent = "no response from background SW"; return; }
      if (resp.ok) render(resp.data);
      else status.textContent = "extract error: " + resp.error;
    });
  }

  mount();
  // Give heavy-JS pages a moment to settle before snapshotting the rendered DOM.
  setTimeout(extract, 2500);
})();
