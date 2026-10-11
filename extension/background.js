// Wire Copy extension — service worker.
//
// All the real work happens here (not in the popup) so a popup that closes
// mid-save cannot drop the article. The wire protocol with the desktop app is
// documented in bead workspace-m7aml; the only transport is Chrome native
// messaging to the locally installed app. Nothing in this extension talks to
// the network.
'use strict'

const HOST = 'com.wirecopy.reader'
const PROTOCOL_VERSION = 1
// Chrome allows 64 MiB browser->host; keep headroom for the JSON envelope/escaping.
const MAX_HTML_BYTES = 60 * 1024 * 1024
const BADGE_MS = 2000

// A badge left over from a worker that was stopped mid-flash must not linger.
chrome.action.setBadgeText({ text: '' })

chrome.runtime.onInstalled.addListener(details => {
  if (details.reason === 'install') {
    chrome.tabs.create({ url: chrome.runtime.getURL('welcome.html') })
  }
})

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  // Only our own pages (popup, welcome) may drive the worker.
  if (sender.id !== chrome.runtime.id || !message) return false

  if (message.type === 'save-active-tab') {
    saveActiveTab().then(sendResponse, e => sendResponse(failure(e)))
    return true
  }
  if (message.type === 'hello') {
    hello().then(sendResponse, e => sendResponse(failure(e)))
    return true
  }
  return false
})

// ── results (the popup/welcome pages render these, never raw host replies) ──
//   { ok:true,  state:'saved', title, words, source, alreadySaved }
//   { ok:true,  state:'connected', appVersion, readingListCount }
//   { ok:false, state:'cant-save', reason }
//   { ok:false, state:'not-connected' }
//   { ok:false, state:'error', message }
const cantSave = reason => ({ ok: false, state: 'cant-save', reason })
const notConnected = () => ({ ok: false, state: 'not-connected' })
const errorResult = message => ({ ok: false, state: 'error', message })
const failure = e => errorResult((e && e.message) || String(e))

// ── native messaging ────────────────────────────────────────────────────────

// One-shot request/reply. Resolves { reply } or { transportError } — never rejects.
function sendNative (message) {
  return new Promise(resolve => {
    try {
      chrome.runtime.sendNativeMessage(HOST, message, reply => {
        const err = chrome.runtime.lastError
        if (err) resolve({ transportError: err.message || String(err) })
        else resolve({ reply })
      })
    } catch (e) {
      resolve({ transportError: (e && e.message) || String(e) })
    }
  })
}

// The errors Chrome raises when the app side simply is not there: host manifest
// not registered, our extension not in its allowed_origins, the host failing to
// launch (registered path points at nothing), or the host process dying before
// it answered.
function isAppMissing (transportError) {
  const t = transportError.toLowerCase()
  return t.includes('native messaging host not found') ||
         t.includes('native messaging host is forbidden') ||
         t.includes('failed to start native messaging host') ||
         t.includes('native host has exited')
}

// Turn a sendNative() outcome into either a host reply object or a final result
// to hand back to the caller ({ result }).
function interpret ({ reply, transportError }) {
  if (transportError) {
    return { result: isAppMissing(transportError) ? notConnected() : errorResult(transportError) }
  }
  if (!reply || typeof reply !== 'object') {
    return { result: errorResult('Wire Copy did not answer.') }
  }
  if (reply.ok === false) {
    return { result: errorResult(reply.error || 'Wire Copy could not save this page.') }
  }
  return { reply }
}

async function hello () {
  const { reply, result } = interpret(await sendNative({
    type: 'hello',
    version: PROTOCOL_VERSION,
    extensionVersion: chrome.runtime.getManifest().version
  }))
  if (result) return result
  return {
    ok: true,
    state: 'connected',
    appVersion: reply.appVersion || '',
    readingListCount: Number.isFinite(reply.readingListCount) ? reply.readingListCount : 0
  }
}

// ── save ────────────────────────────────────────────────────────────────────

const isWebUrl = url => /^https?:\/\//i.test(url || '')
const NON_WEB_REASON = 'Wire Copy can only save pages from websites. Open an article and try again.'

// Pages the scripting API refuses to touch even though they are https.
function isRestrictedWebUrl (url) {
  try {
    const u = new URL(url)
    return u.hostname === 'chromewebstore.google.com' ||
           (u.hostname === 'chrome.google.com' && u.pathname.startsWith('/webstore')) ||
           u.hostname === 'microsoftedge.microsoft.com'
  } catch {
    return true
  }
}

// Runs inside the page (isolated world, top frame). Must be self-contained.
function readPage () {
  return {
    url: location.href,
    title: document.title,
    html: document.documentElement.outerHTML,
    contentType: document.contentType
  }
}

function utf8Bytes (s) {
  // UTF-8 is never shorter than the UTF-16 length, and at most 3x it.
  if (s.length > MAX_HTML_BYTES) return s.length
  if (s.length * 3 <= MAX_HTML_BYTES) return s.length * 3 // cannot exceed, skip the copy
  return new TextEncoder().encode(s).length
}

async function saveActiveTab () {
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true })
  if (!tab || tab.id == null) return cantSave('There is no page open to save.')
  if (!isWebUrl(tab.url) || isRestrictedWebUrl(tab.url)) {
    return cantSave(NON_WEB_REASON)
  }

  let page
  try {
    const [injection] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: readPage })
    page = injection && injection.result
  } catch (e) {
    // The browser blocks scripting here (PDF viewer, error pages, protected pages, no activeTab grant).
    return cantSave('The browser does not let extensions read this page.')
  }
  if (!page || typeof page.html !== 'string') return cantSave('The browser does not let extensions read this page.')
  if (!isWebUrl(page.url)) return cantSave(NON_WEB_REASON)
  if (!/^(text\/html|application\/xhtml\+xml)$/i.test(page.contentType || '')) {
    return cantSave('This is not a web page Wire Copy can read (for example a PDF or an image).')
  }
  if (utf8Bytes(page.html) > MAX_HTML_BYTES) {
    return cantSave('This page is too large to save (over 60 MB).')
  }

  const { reply, result } = interpret(await sendNative({
    type: 'save',
    version: PROTOCOL_VERSION,
    url: page.url,
    title: page.title,
    html: page.html,
    extensionVersion: chrome.runtime.getManifest().version
  }))
  if (result) return result

  flashBadge()
  return {
    ok: true,
    state: 'saved',
    title: reply.title || page.title || page.url,
    words: Number.isFinite(reply.words) ? reply.words : 0,
    source: reply.source || 'none',
    alreadySaved: reply.alreadySaved === true
  }
}

function flashBadge () {
  chrome.action.setBadgeBackgroundColor({ color: '#00af00' })
  chrome.action.setBadgeText({ text: '✓' })
  setTimeout(() => chrome.action.setBadgeText({ text: '' }), BADGE_MS)
}
