// Wire Copy welcome page — opened once on install. Asks the service worker to say
// "hello" to the desktop app (over native messaging) on load and every 2 s until it
// answers, then turns green. Nothing here touches the network.
'use strict'

const $ = id => document.getElementById(id)
const POLL_MS = 2000

let connected = false
let inFlight = false
let timer = null

function articlesLabel (n) {
  return n === 1 ? '1 article in your Reading List' : `${n.toLocaleString()} articles in your Reading List`
}

function showConnected (r) {
  connected = true
  clearInterval(timer)
  $('count').textContent = articlesLabel(r.readingListCount || 0)
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

$('check-again').addEventListener('click', check)
check()
timer = setInterval(check, POLL_MS)
