/* TermReader · box-drawing helper
 *
 * Renders cell-perfect Unicode boxes inside a <pre> by:
 *   - Building horizontal rules programmatically (no hand-typed strings)
 *   - Wrapping every box in a forced font stack (JetBrains Mono)
 *   - Disabling ligatures + contextual alternates so ─ stays single-cell
 *   - Keeping each row in a single text run (no inline color spans on chars
 *     that the box-drawing font may render differently from a fallback)
 *
 * Usage:
 *   TRBox.draw('#myBox', {
 *     style: 'thin' | 'thick' | 'double' | 'round',
 *     width: 40,                   // total cells incl. corners
 *     color: '#5fff5f',            // border foreground
 *     rows: [                      // each row = string OR { text, color }
 *       { text: 'Loading page...', color: '#00d700' },
 *       'arstechnica.com/...',
 *     ]
 *   });
 *
 * Each row is padded with spaces to (width - 2) cells, then framed with
 * the vertical bar on each side.
 */
(function (root) {
  const STYLES = {
    thin:   { tl: '\u250C', tr: '\u2510', bl: '\u2514', br: '\u2518', h: '\u2500', v: '\u2502' },
    thick:  { tl: '\u250F', tr: '\u2513', bl: '\u2517', br: '\u251B', h: '\u2501', v: '\u2503' },
    double: { tl: '\u2554', tr: '\u2557', bl: '\u255A', br: '\u255D', h: '\u2550', v: '\u2551' },
    round:  { tl: '\u256D', tr: '\u256E', bl: '\u2570', br: '\u256F', h: '\u2500', v: '\u2502' },
  };

  function cellLen(s) { return [...s].length; }
  function rule(width, glyph) { return glyph.repeat(width - 2); }
  function padRow(text, innerWidth) {
    const len = cellLen(text);
    if (len >= innerWidth) return [...text].slice(0, innerWidth).join('');
    return text + ' '.repeat(innerWidth - len);
  }

  function build({ style = 'round', width = 40, rows = [], color = '#5fff5f' }) {
    const s = STYLES[style] || STYLES.round;
    const inner = Math.max(0, width - 2);
    const lines = [];
    lines.push({ chrome: s.tl + rule(width, s.h) + s.tr, color });
    for (const r of rows) {
      const text = typeof r === 'string' ? r : r.text;
      const rowColor = typeof r === 'string' ? null : (r.color || null);
      lines.push({
        chrome: s.v,
        body: padRow(text || '', inner),
        chromeR: s.v,
        color,
        rowColor,
      });
    }
    lines.push({ chrome: s.bl + rule(width, s.h) + s.br, color });
    return lines;
  }

  function draw(selector, opts) {
    const el = document.querySelector(selector);
    if (!el) return;
    el.classList.add('tr-box-pre');
    const lines = build(opts);
    const html = lines.map(l => {
      if (l.body == null) {
        return `<span style="color:${l.color}">${l.chrome}</span>`;
      }
      const bodyColor = l.rowColor || '#00d700';
      return (
        `<span style="color:${l.color}">${l.chrome}</span>` +
        `<span style="color:${bodyColor}">${l.body}</span>` +
        `<span style="color:${l.color}">${l.chromeR}</span>`
      );
    }).join('\n');
    el.innerHTML = html;
  }

  root.TRBox = { build, draw, STYLES };
})(window);
