// Wire Copy popup — saves the current page the moment it opens. The service worker
// does the real work (so closing this popup cannot lose a save); this file only
// shows the outcome. Nothing here touches the network.
'use strict'

const $ = id => document.getElementById(id)
const STATES = ['saving', 'saved', 'cant-save', 'not-connected', 'error']

function show (state) {
  for (const s of STATES) $(s).hidden = s !== state
}

function wordsLabel (n) {
  return n === 1 ? '1 word' : `${n.toLocaleString()} words`
}

function render (r) {
  if (!r) r = { state: 'error', message: 'Wire Copy did not respond.' }
  switch (r.state) {
    case 'saved':
      $('saved-title').textContent = r.title
      $('saved-words').textContent = r.words > 0 ? wordsLabel(r.words) : ''
      $('saved-words').hidden = !(r.words > 0)
      $('saved-note-already').hidden = !r.alreadySaved
      $('saved-note-none').hidden = r.source !== 'none'
      show('saved')
      break
    case 'cant-save':
      $('cant-save-reason').textContent = r.reason || ''
      show('cant-save')
      break
    case 'not-connected':
      show('not-connected')
      break
    default:
      $('error-message').textContent = r.message || 'Something went wrong.'
      show('error')
  }
}

async function save () {
  show('saving')
  let result
  try {
    result = await chrome.runtime.sendMessage({ type: 'save-active-tab' })
  } catch (e) {
    result = { state: 'error', message: (e && e.message) || String(e) }
  }
  render(result)
}

$('retry').addEventListener('click', save)
$('connect').addEventListener('click', async () => {
  await chrome.tabs.create({ url: chrome.runtime.getURL('welcome.html') })
  window.close()
})

save()
