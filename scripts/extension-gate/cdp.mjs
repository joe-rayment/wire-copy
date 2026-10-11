#!/usr/bin/env node
// Minimal Chrome DevTools Protocol client for the extension gate.
// Node 22 only: global fetch + WebSocket, no npm packages.
//
//   node cdp.mjs <port> targets                          JSON list of {type,url,id,title}
//   node cdp.mjs <port> new <url>                        open a new tab, print its target
//   node cdp.mjs <port> eval <url-substring> <js>        Runtime.evaluate in the first matching
//                                                        target (awaitPromise, returnByValue)
//   node cdp.mjs <port> sw-eval <extension-id> <js>      same, in that extension's service worker
//   node cdp.mjs <port> offline-check <url-substring>    in that page: fetch https://example.com
//                                                        -> prints "online" or "offline"
//   node cdp.mjs <port> wait-target <url-substring> <ms> exit 0 once a target URL contains it,
//                                                        1 on timeout
//
// `eval` attaches with Target.attachToTarget {flatten:true} over the browser-level websocket, so
// it works for pages, iframes, extension pages and service workers alike.
// CDP_WAIT_MS (default 5000) is how long eval/sw-eval poll for the target to appear (an MV3
// service worker is only listed while it is awake).

const [, , portArg, cmd, ...rest] = process.argv;
const port = Number(portArg);
const base = `http://127.0.0.1:${port}`;

function die(msg, code = 1) {
  console.error(msg);
  process.exit(code);
}

async function listTargets() {
  const r = await fetch(`${base}/json/list`);
  if (!r.ok) die(`GET /json/list -> HTTP ${r.status}`);
  return await r.json();
}

// /json/list hides service workers on some builds; Target.getTargets (browser ws) sees everything.
async function allTargets(browser) {
  const res = await browser.send("Target.getTargets");
  return res.targetInfos.map((t) => ({ type: t.type, url: t.url, id: t.targetId, title: t.title }));
}

async function connectBrowser() {
  const v = await (await fetch(`${base}/json/version`)).json();
  const ws = new WebSocket(v.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    ws.addEventListener("open", resolve, { once: true });
    ws.addEventListener("error", () => reject(new Error("websocket error")), { once: true });
  });
  let nextId = 1;
  const pending = new Map();
  ws.addEventListener("message", (ev) => {
    const msg = JSON.parse(ev.data);
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.error) reject(new Error(`${msg.error.message} (${msg.error.code})`));
      else resolve(msg.result);
    }
  });
  return {
    send(method, params = {}, sessionId) {
      const id = nextId++;
      return new Promise((resolve, reject) => {
        pending.set(id, { resolve, reject });
        ws.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
      });
    },
    close() {
      ws.close();
    },
  };
}

async function findTarget(browser, predicate, what) {
  const deadline = Date.now() + Number(process.env.CDP_WAIT_MS ?? 5000);
  for (;;) {
    const t = (await allTargets(browser)).find(predicate);
    if (t) return t;
    if (Date.now() > deadline) die(`no target found for ${what}`);
    await new Promise((r) => setTimeout(r, 250));
  }
}

async function evalIn(browser, target, js) {
  const { sessionId } = await browser.send("Target.attachToTarget", { targetId: target.id, flatten: true });
  const res = await browser.send(
    "Runtime.evaluate",
    { expression: js, awaitPromise: true, returnByValue: true },
    sessionId,
  );
  await browser.send("Target.detachFromTarget", { sessionId }).catch(() => {});
  if (res.exceptionDetails) {
    const d = res.exceptionDetails;
    die(`evaluation threw: ${d.exception?.description ?? d.text}`);
  }
  return res.result.value;
}

function printValue(v) {
  console.log(v === undefined ? "undefined" : JSON.stringify(v));
}

const watchdog = setTimeout(() => die("cdp.mjs: timed out after 60 s"), 60000);

try {
  if (!port || !cmd) die("usage: node cdp.mjs <port> targets|new|eval|sw-eval|offline-check|wait-target ...", 2);

  if (cmd === "targets") {
    const list = await listTargets();
    console.log(JSON.stringify(list.map((t) => ({ type: t.type, url: t.url, id: t.id, title: t.title })), null, 1));
  } else if (cmd === "new") {
    const [url] = rest;
    if (!url) die("usage: new <url>", 2);
    const r = await fetch(`${base}/json/new?${url}`, { method: "PUT" });
    if (!r.ok) die(`PUT /json/new -> HTTP ${r.status}`);
    const t = await r.json();
    console.log(JSON.stringify({ type: t.type, url: t.url, id: t.id }));
  } else if (cmd === "eval" || cmd === "sw-eval" || cmd === "offline-check") {
    let predicate, what, js;
    if (cmd === "sw-eval") {
      const [extId, code] = rest;
      if (!extId || code === undefined) die("usage: sw-eval <extension-id> <js>", 2);
      const prefix = `chrome-extension://${extId}/`;
      predicate = (t) => t.type === "service_worker" && t.url.startsWith(prefix);
      what = `service_worker of extension ${extId} (asleep? trigger an extension event first)`;
      js = code;
    } else {
      const [sub, code] = rest;
      if (!sub || (cmd === "eval" && code === undefined)) die(`usage: ${cmd} <url-substring>${cmd === "eval" ? " <js>" : ""}`, 2);
      predicate = (t) => t.url.includes(sub) && t.type !== "service_worker" && t.type !== "browser";
      what = `url containing "${sub}"`;
      js =
        cmd === "eval"
          ? code
          : "fetch('https://example.com',{mode:'no-cors',cache:'no-store'}).then(()=>'online',()=>'offline')";
    }
    const browser = await connectBrowser();
    const target = await findTarget(browser, predicate, what);
    const value = await evalIn(browser, target, js);
    if (cmd === "offline-check") console.log(value);
    else printValue(value);
    browser.close();
  } else if (cmd === "wait-target") {
    const [sub, msArg] = rest;
    if (!sub || !msArg) die("usage: wait-target <url-substring> <timeout-ms>", 2);
    const deadline = Date.now() + Number(msArg);
    let found = false;
    while (!found && Date.now() <= deadline) {
      try {
        found = (await listTargets()).some((t) => t.url.includes(sub));
      } catch {
        // CDP endpoint not up yet; keep polling until the deadline.
      }
      if (!found) await new Promise((r) => setTimeout(r, 250));
    }
    process.exit(found ? 0 : 1);
  } else {
    die(`unknown command: ${cmd}`, 2);
  }
} catch (e) {
  die(`cdp.mjs: ${e.message}`);
}
clearTimeout(watchdog);
process.exit(0);
