// MV3 service worker: bridges the content-script overlay to the local WireCopy backend.
// The SW (an extension context with host_permissions) can fetch http://127.0.0.1 from an https page
// without mixed-content/CORS blocks — which a page-context fetch could not.
const BACKEND = "http://127.0.0.1:5181/extract";

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg && msg.type === "extract") {
    fetch(BACKEND, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ url: msg.url, html: msg.html }),
    })
      .then((r) => r.json())
      .then((data) => sendResponse({ ok: true, data }))
      .catch((e) => sendResponse({ ok: false, error: String(e) }));
    return true; // keep the message channel open for the async response
  }
  if (msg && msg.type === "navigate" && sender.tab) {
    chrome.tabs.update(sender.tab.id, { url: msg.url });
  }
  return undefined;
});
