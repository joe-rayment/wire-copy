// Target states for the Routines v3 screens (epic workspace-6ifpp). Each entry is the launcher-pane
// state a screen pushes (state = LauncherViewState as the pane receives it).
// The pane takes its bar, counts, footer label/hints and box legends from state (barPlaceholder, barHidden,
// countLabel, footerCount, appLabel, hints, slot.legend), so a scenario is { name, state } and nothing else.
// Render them with the REAL pane: see main.js.
const base = (o) => Object.assign({ mode: 'routine', modeVariant: 'amber', columns: 2, selectedIndex: 0, bookmarkCount: 0, footer: {}, version: '' }, o)
const tile = (badge, title, hint, more = {}) => Object.assign({ kind: 'routine', badge: String(badge), title, hint }, more)
const half = (pos, badge, title, hint, more = {}) => Object.assign({ kind: 'routines', half: pos, badge: String(badge), title, hint }, more)
module.exports = [
  {
    name: 'home',
    state: base({
      screen: 'routines',
      barPlaceholder: 'New routine: type a name…', countLabel: '4 routines', appLabel: 'Routines',
      hints: [{ key: 'Enter', label: 'open' }, { key: '1-9', label: 'jump' }, { key: 'n', label: 'new' }, { key: 'r', label: 'run now' }, { key: 'Space', label: 'on/off' }, { key: 'd', label: 'delete' }, { key: '?', label: 'all keys' }],
      eyebrow: 'Routines', title: 'Routines', tagline: '3 on · next Morning brief today 07:00',
      footer: { badge: '1 needs attention' },
      slots: [
        tile(1, 'MORNING BRIEF', 'Weekdays · 07:00 · next today 07:00', { detail: '2 sections · 1 site · last ✓ today 07:02' }),
        tile(2, 'WEEKEND LONG READS', 'Sat, Sun · 09:30 · next Sat 09:30', { alert: '✗ audio-engine isn’t running · Enter to fix' }),
        tile(3, 'TECH DIGEST', 'running · narrating story 4 of 12', { detail: '3 sections · 2 sites' }),
        tile(4, 'DISASTER WATCH', 'off · Daily · 18:00', { detail: '1 section · 1 site · last ✓ Mon 18:01', disabled: true })
      ]
    })
  },
  {
    name: 'home-empty',
    state: base({
      screen: 'routines',
      barPlaceholder: 'New routine: type a name…', countLabel: 'no routines yet', appLabel: 'Routines', hints: [{ key: 'n', label: 'new' }, { key: 'Esc', label: 'back' }, { key: '?', label: 'all keys' }],
      eyebrow: 'Routines', title: 'Routines', tagline: 'Sections you pick, read to you on a schedule',
      selectedIndex: -1, urlTyping: { buffer: 'Morning brie', cursor: 12 },
      setupHint: '→ Name it · pick sites and sections · choose when · episodes land in your podcast feed',
      slots: []
    })
  },
  {
    name: 'routine',
    state: base({
      screen: 'routine',
      barPlaceholder: 'Add a site: type an address…', countLabel: '3 sections · 2 sites', footerCount: '', appLabel: 'Routine', hints: [{ key: 'Enter', label: 'open' }, { key: '1-9', label: 'jump' }, { key: 'a', label: 'add a site' }, { key: 'r', label: 'run now' }, { key: 'Space', label: 'on/off' }, { key: 'd', label: 'remove' }, { key: '?', label: 'all keys' }],
      eyebrow: 'Routines › Morning brief', title: 'Morning brief', tagline: 'Weekdays at 07:00 · next today 07:00',
      footer: { status: 'on' },
      slots: [
        tile(1, 'ROUTINE FIXTURE GAZETTE', '127.0.0.1', { detail: 'World (all 9) · Science (top 3)' }),
        half('top', 2, 'NAME', 'Morning brief', { legend: 'This routine' }),
        half('bottom', 3, 'SCHEDULE', 'Weekdays · 07:00 · next today 07:00'),
        tile(4, 'THE GUARDIAN', 'www.theguardian.com', { detail: 'Headlines (#1, #3)' }),
        half('top', 5, 'PODCAST', 'Morning brief · episodes go to this feed', { legend: 'Output' }),
        half('bottom', 6, 'LAST RUN', '✓ published today 07:02 · 12 stories · 3m 10s')
      ]
    })
  },
  {
    name: 'routine-rename',
    state: base({
      screen: 'routine',
      barPlaceholder: '', countLabel: '3 sections · 2 sites', appLabel: 'Routine', hints: [{ key: 'Enter', label: 'save' }, { key: 'Esc', label: 'keep the old name' }],
      eyebrow: 'Routines › Morning brief', title: 'Morning brief', tagline: 'Weekdays at 07:00 · next today 07:00',
      footer: { status: 'on' }, selectedIndex: -1, urlTyping: { buffer: 'Name: AM brief', cursor: 14 },
      slots: [
        tile(1, 'ROUTINE FIXTURE GAZETTE', '127.0.0.1', { detail: 'World (all 9) · Science (top 3)' }),
        half('top', 2, 'NAME', 'Morning brief', { legend: 'This routine' }),
        half('bottom', 3, 'SCHEDULE', 'Weekdays · 07:00 · next today 07:00'),
        tile(4, 'THE GUARDIAN', 'www.theguardian.com', { detail: 'Headlines (#1, #3)' }),
        half('top', 5, 'PODCAST', 'Morning brief · episodes go to this feed', { legend: 'Output' }),
        half('bottom', 6, 'LAST RUN', '✓ published today 07:02 · 12 stories · 3m 10s')
      ]
    })
  },
  {
    name: 'schedule',
    state: base({
      screen: 'schedule',
      barPlaceholder: 'Time: 07:00', countLabel: 'next today 07:00', appLabel: 'Schedule', hints: [{ key: 'Enter', label: 'choose' }, { key: 'Space', label: 'day on/off' }, { key: 't', label: 'time' }, { key: 'Esc', label: 'done' }],
      eyebrow: 'Routines › Morning brief › Schedule', title: 'Weekdays at 07:00', tagline: 'Every change is saved as you make it',
      columns: 4, selectedIndex: 1,
      slots: [
        tile(1, 'EVERY DAY', 'all seven'),
        tile(2, 'WEEKDAYS', 'Mon–Fri · current'),
        tile(3, 'WEEKENDS', 'Sat, Sun'),
        { kind: 'empty' },
        tile(4, 'MON', 'on'), tile(5, 'TUE', 'on'), tile(6, 'WED', 'on'), tile(7, 'THU', 'on'),
        tile(8, 'FRI', 'on'), tile(9, 'SAT', 'off', { disabled: true }), tile('', 'SUN', 'off', { disabled: true }), { kind: 'empty' }
      ]
    })
  },
  {
    name: 'add-site',
    state: base({
      screen: 'addSite',
      barPlaceholder: 'Go to URL…', countLabel: '2 bookmarks', appLabel: 'Add a site', hints: [{ key: 'Enter', label: 'pick this site' }, { key: '1-9', label: 'jump' }, { key: 'o', label: 'type an address' }, { key: 'Esc', label: 'back' }],
      eyebrow: 'Routines › Morning brief › Add a site', title: 'Add a site', tagline: 'Pick one of your sites, or type an address',
      slots: [
        tile(1, 'ROUTINE FIXTURE GAZETTE', '127.0.0.1', { detail: 'already in this routine · 2 sections' }),
        tile(2, 'THE GUARDIAN', 'www.theguardian.com')
      ]
    })
  }

  ,{
    name: 'run-running',
    state: base({
      screen: 'run',
      barHidden: true, countLabel: '4 of 12 stories', footerCount: '', appLabel: 'Run', hints: [{ key: 'x', label: 'cancel' }, { key: 'Esc', label: 'leave it running' }, { key: 'l', label: 'log' }],
      eyebrow: 'Routines › Morning brief › Run', title: 'Running…', tagline: 'Started 07:00 · 1m 12s so far',
      columns: 3, selectedIndex: 3,
      slots: [
        { kind: 'routine', title: 'NARRATION', hint: '✓ audio-engine is ready' },
        { kind: 'routine', title: 'WORLD', hint: '✓ 9 stories · Routine Fixture Gazette' },
        { kind: 'routine', title: 'SCIENCE', hint: '✓ 3 stories · Routine Fixture Gazette' },
        { kind: 'routine', title: 'NARRATE', hint: 'story 4 of 12', detail: '“Dynamite used to stop the fires”' },
        { kind: 'routine', title: 'PUBLISH', hint: 'waiting · feed “Morning brief”', disabled: true }
      ]
    })
  },
  {
    name: 'run-failed',
    state: base({
      screen: 'run',
      barHidden: true, countLabel: 'failed today 23:05', footerCount: '', appLabel: 'Run', hints: [{ key: 'Enter', label: 'open' }, { key: '1-9', label: 'jump' }, { key: 'r', label: 'run again' }, { key: 'l', label: 'log' }, { key: 'Esc', label: 'back' }],
      eyebrow: 'Routines › Weekend long reads › Last run', title: 'Couldn’t narrate', tagline: 'audio-engine isn’t running, so nothing could read the stories aloud',
      slots: [
        tile(1, 'START AUDIO-ENGINE', 'the fix · Enter starts it, then run again'),
        half('top', 2, 'RUN AGAIN', 'same sections, now', { legend: 'This run' }),
        half('bottom', 3, 'OPEN THE LOG', '14 lines from this run'),
        tile(4, 'ARTS', '✓ 6 stories · Routine Fixture Gazette', { detail: 'collected before the failure' }),
        half('top', 5, 'SUN 09:30', '✓ published · 6 stories · 2m 41s', { legend: 'Earlier runs' }),
        half('bottom', 6, 'SAT 09:31', '✓ published · 7 stories · 3m 02s')
      ]
    })
  },
  {
    name: 'run-ok',
    state: base({
      screen: 'run',
      barHidden: true, countLabel: 'published today 07:02', footerCount: '', appLabel: 'Run', hints: [{ key: 'Enter', label: 'open' }, { key: '1-9', label: 'jump' }, { key: 'r', label: 'run again' }, { key: 'l', label: 'log' }, { key: 'Esc', label: 'back' }],
      eyebrow: 'Routines › Morning brief › Last run', title: 'Published', tagline: '12 stories · 14 min of audio · took 3m 10s',
      slots: [
        tile(1, 'PLAY EPISODE', 'Morning brief · Thu 12 Nov'),
        half('top', 2, 'COPY FEED URL', 'paste it into your podcast app', { legend: 'This run' }),
        half('bottom', 3, 'SHOW FILE', 'Morning brief · Thu 12 Nov.m4a'),
        tile(4, 'WORLD', '✓ 9 stories · Routine Fixture Gazette'),
        half('top', 5, 'WED 07:01', '✓ published · 11 stories · 2m 58s', { legend: 'Earlier runs' }),
        half('bottom', 6, 'TUE 07:00', '✓ published · 12 stories · 3m 21s'),
        tile(7, 'SCIENCE', '✓ 3 stories · Routine Fixture Gazette')
      ]
    })
  }
]
