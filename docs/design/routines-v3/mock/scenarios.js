// Target states for the Routines v3 screens (epic workspace-6ifpp). Each entry is the launcher-pane
// state a screen should push (state = LauncherViewState as the pane receives it) plus the text slots
// the pane hard-codes today and the build must make state-driven:
//   bar       -> placeholder of the bar ("Go to URL…" today); noBar hides the bar
//   count     -> header-right / footer-right label ("N bookmarks" today); footRight overrides the footer one
//   app,hints -> footer label and [key]:label hints (launcher's own HINTS today)
//   legends   -> legend of each boxed pair of half-tiles ("Yours" today)
// Render them with the REAL pane: see main.js.
const base = (o) => Object.assign({ mode: 'routine', modeVariant: 'amber', columns: 2, selectedIndex: 0, bookmarkCount: 0, footer: {}, version: '' }, o)
const tile = (badge, title, hint, more = {}) => Object.assign({ kind: 'routine', badge: String(badge), title, hint }, more)
const half = (pos, badge, title, hint, more = {}) => Object.assign({ kind: 'routines', half: pos, badge: String(badge), title, hint }, more)
module.exports = [
  {
    name: 'home',
    bar: 'New routine: type a name…', count: '4 routines', app: 'Routines',
    hints: [['Enter', 'open'], ['1-9', 'jump'], ['n', 'new'], ['r', 'run now'], ['Space', 'on/off'], ['d', 'delete'], ['?', 'all keys']],
    state: base({
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
    bar: 'New routine: type a name…', count: 'no routines yet', app: 'Routines',
    hints: [['n', 'new'], ['Esc', 'back'], ['?', 'all keys']],
    state: base({
      eyebrow: 'Routines', title: 'Routines', tagline: 'Sections you pick, read to you on a schedule',
      selectedIndex: -1, urlTyping: { buffer: 'Morning brie', cursor: 12 },
      setupHint: '→ Name it · pick sites and sections · choose when · episodes land in your podcast feed',
      slots: []
    })
  },
  {
    name: 'routine',
    bar: 'Add a site: type an address…', count: '3 sections · 2 sites', footRight: '', app: 'Routine',
    legends: ['This routine', 'Output'],
    hints: [['Enter', 'open'], ['1-9', 'jump'], ['a', 'add a site'], ['r', 'run now'], ['Space', 'on/off'], ['d', 'remove'], ['?', 'all keys']],
    state: base({
      eyebrow: 'Routines › Morning brief', title: 'Morning brief', tagline: 'Weekdays at 07:00 · next today 07:00',
      footer: { status: 'on' },
      slots: [
        tile(1, 'ROUTINE FIXTURE GAZETTE', '127.0.0.1', { detail: 'World (all 9) · Science (top 3)' }),
        half('top', 2, 'NAME', 'Morning brief'),
        half('bottom', 3, 'SCHEDULE', 'Weekdays · 07:00 · next today 07:00'),
        tile(4, 'THE GUARDIAN', 'www.theguardian.com', { detail: 'Headlines (#1, #3)' }),
        half('top', 5, 'PODCAST', 'Morning brief · episodes go to this feed'),
        half('bottom', 6, 'LAST RUN', '✓ published today 07:02 · 12 stories · 3m 10s')
      ]
    })
  },
  {
    name: 'routine-rename',
    bar: '', count: '3 sections · 2 sites', app: 'Routine',
    legends: ['This routine', 'Output'],
    hints: [['Enter', 'save'], ['Esc', 'keep the old name']],
    state: base({
      eyebrow: 'Routines › Morning brief', title: 'Morning brief', tagline: 'Weekdays at 07:00 · next today 07:00',
      footer: { status: 'on' }, selectedIndex: -1, urlTyping: { buffer: 'Name: AM brief', cursor: 14 },
      slots: [
        tile(1, 'ROUTINE FIXTURE GAZETTE', '127.0.0.1', { detail: 'World (all 9) · Science (top 3)' }),
        half('top', 2, 'NAME', 'Morning brief'),
        half('bottom', 3, 'SCHEDULE', 'Weekdays · 07:00 · next today 07:00'),
        tile(4, 'THE GUARDIAN', 'www.theguardian.com', { detail: 'Headlines (#1, #3)' }),
        half('top', 5, 'PODCAST', 'Morning brief · episodes go to this feed'),
        half('bottom', 6, 'LAST RUN', '✓ published today 07:02 · 12 stories · 3m 10s')
      ]
    })
  },
  {
    name: 'schedule',
    bar: 'Time: 07:00', count: 'next today 07:00', app: 'Schedule',
    hints: [['Enter', 'choose'], ['Space', 'day on/off'], ['t', 'time'], ['Esc', 'done']],
    state: base({
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
    bar: 'Go to URL…', count: '2 bookmarks', app: 'Add a site',
    hints: [['Enter', 'pick this site'], ['1-9', 'jump'], ['o', 'type an address'], ['Esc', 'back']],
    state: base({
      eyebrow: 'Routines › Morning brief › Add a site', title: 'Add a site', tagline: 'Pick one of your sites, or type an address',
      slots: [
        tile(1, 'ROUTINE FIXTURE GAZETTE', '127.0.0.1', { detail: 'already in this routine · 2 sections' }),
        tile(2, 'THE GUARDIAN', 'www.theguardian.com')
      ]
    })
  }

  ,{
    name: 'run-running', noBar: true, count: '4 of 12 stories', footRight: '', app: 'Run',
    hints: [['x', 'cancel'], ['Esc', 'leave it running'], ['l', 'log']],
    state: base({
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
    name: 'run-failed', noBar: true, count: 'failed today 23:05', footRight: '', app: 'Run',
    legends: ['This run', 'Earlier runs'],
    hints: [['Enter', 'open'], ['1-9', 'jump'], ['r', 'run again'], ['l', 'log'], ['Esc', 'back']],
    state: base({
      eyebrow: 'Routines › Weekend long reads › Last run', title: 'Couldn’t narrate', tagline: 'audio-engine isn’t running, so nothing could read the stories aloud',
      slots: [
        tile(1, 'START AUDIO-ENGINE', 'the fix · Enter starts it, then run again'),
        half('top', 2, 'RUN AGAIN', 'same sections, now'),
        half('bottom', 3, 'OPEN THE LOG', '14 lines from this run'),
        tile(4, 'ARTS', '✓ 6 stories · Routine Fixture Gazette', { detail: 'collected before the failure' }),
        half('top', 5, 'SUN 09:30', '✓ published · 6 stories · 2m 41s'),
        half('bottom', 6, 'SAT 09:31', '✓ published · 7 stories · 3m 02s')
      ]
    })
  },
  {
    name: 'run-ok', noBar: true, count: 'published today 07:02', footRight: '', app: 'Run',
    legends: ['This run', 'Earlier runs'],
    hints: [['Enter', 'open'], ['1-9', 'jump'], ['r', 'run again'], ['l', 'log'], ['Esc', 'back']],
    state: base({
      eyebrow: 'Routines › Morning brief › Last run', title: 'Published', tagline: '12 stories · 14 min of audio · took 3m 10s',
      slots: [
        tile(1, 'PLAY EPISODE', 'Morning brief · Thu 12 Nov'),
        half('top', 2, 'COPY FEED URL', 'paste it into your podcast app'),
        half('bottom', 3, 'SHOW FILE', 'Morning brief · Thu 12 Nov.m4a'),
        tile(4, 'WORLD', '✓ 9 stories · Routine Fixture Gazette'),
        half('top', 5, 'WED 07:01', '✓ published · 11 stories · 2m 58s'),
        half('bottom', 6, 'TUE 07:00', '✓ published · 12 stories · 3m 21s'),
        tile(7, 'SCIENCE', '✓ 3 stories · Routine Fixture Gazette')
      ]
    })
  }
]
