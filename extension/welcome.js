// Wire Copy welcome page — opened on install (or taken over from the local
// add-extension guide). Asks the service worker to say "hello" to the desktop app
// (over native messaging) on load and every 2 s until it answers, then turns green
// and ticks off the two setup steps live: the W button landing on the toolbar and
// the first article saved. Nothing here touches the network.
'use strict'

const $ = id => document.getElementById(id)
const POLL_MS = 2000
const PIN_POLL_MS = 1000

let connected = false
let inFlight = false
let timer = null

// ── which browser ───────────────────────────────────────────────────────────

// UA-CH brand → the name we print. Chrome/Edge/Brave/Opera all also list
// "Chromium", so the specific names come first.
const BRANDS = [
  ['Google Chrome', 'Chrome'],
  ['Microsoft Edge', 'Edge'],
  ['Brave', 'Brave'],
  ['Opera', 'Opera'],
  ['Chromium', 'Chromium']
]

function browserName () {
  const brands = (navigator.userAgentData && navigator.userAgentData.brands) || []
  const hit = BRANDS.find(([brand]) => brands.some(b => b.brand === brand))
  return hit ? hit[1] : 'your browser'
}

// ── connection ──────────────────────────────────────────────────────────────

function articlesLabel (n) {
  return n === 1 ? '1 article in your Reading List' : `${n.toLocaleString()} articles in your Reading List`
}

function showConnected (r) {
  connected = true
  clearInterval(timer)
  const n = r.readingListCount || 0
  $('lead').textContent = 'Connected to Wire Copy on this computer.' + (n > 0 ? ` · ${articlesLabel(n)}` : '')
  $('waiting').hidden = true
  $('connected').hidden = false
}

function showWaiting (r) {
  const detail = r && r.state === 'error' ? r.message : ''
  $('waiting-detail').textContent = detail || ''
  $('waiting-detail').hidden = !detail
  $('connected').hidden = true
  $('waiting').hidden = false
}

async function check () {
  if (connected || inFlight) return
  inFlight = true
  let result
  try {
    result = await chrome.runtime.sendMessage({ type: 'hello' })
  } catch (e) {
    result = { state: 'error', message: (e && e.message) || String(e) }
  } finally {
    inFlight = false
  }
  if (result && result.state === 'connected') showConnected(result)
  else showWaiting(result)
}

// ── setup steps ─────────────────────────────────────────────────────────────

const stepDone = { pin: false, save: false }

function setStep (name, isDone) {
  stepDone[name] = isDone
  $(`step-${name}`).classList.toggle('is-done', isDone)
  $('all-set').hidden = !(stepDone.pin && stepDone.save)
}

// Step 1: is the W button on the toolbar? Polled, and also event-driven where the
// event exists (Chrome 130+). Unpinning takes the check away again.
async function checkPinned () {
  try {
    const settings = await chrome.action.getUserSettings()
    setStep('pin', settings.isOnToolbar === true)
  } catch {
    // Older browser without the API: leave the step as an instruction.
  }
}

function savedLabel ({ title, alreadySaved }) {
  const quoted = `“${title || 'this page'}”`
  return alreadySaved ? `Already in your Reading List: ${quoted}.` : `Saved ${quoted}.`
}

// Step 2: the service worker broadcasts {type:'saved'} after each successful save.
chrome.runtime.onMessage.addListener((message, sender) => {
  if (sender.id !== chrome.runtime.id || !message || message.type !== 'saved') return
  $('saved-text').textContent = savedLabel(message)
  setStep('save', true)
})

$('title').textContent = `Wire Copy is in ${browserName()}`
$('check-again').addEventListener('click', check)
check()
timer = setInterval(check, POLL_MS)

checkPinned()
setInterval(checkPinned, PIN_POLL_MS)
if (chrome.action.onUserSettingsChanged) {
  chrome.action.onUserSettingsChanged.addListener(checkPinned)
}
