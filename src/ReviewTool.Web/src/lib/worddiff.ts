/** Character ranges [start, end) within a line. */
export type Range = [number, number];

const TOKEN = /\w+|\s+|[^\w\s]/g;
const MAX_TOKENS = 400;

function tokenize(s: string): string[] {
  return s.match(TOKEN) ?? [];
}

/**
 * Word-level diff of two versions of a line. Returns the changed ranges on each side, or null when
 * the lines are too different for intra-line highlighting to help (then the whole line is the change).
 */
export function wordDiff(oldText: string, newText: string): { old: Range[]; new: Range[] } | null {
  if (oldText === newText) return { old: [], new: [] };
  const a = tokenize(oldText);
  const b = tokenize(newText);
  if (a.length === 0 || b.length === 0 || a.length > MAX_TOKENS || b.length > MAX_TOKENS) return null;

  // Longest common subsequence over tokens.
  const n = a.length;
  const m = b.length;
  const dp = Array.from({ length: n + 1 }, () => new Uint16Array(m + 1));
  for (let i = n - 1; i >= 0; i--)
    for (let j = m - 1; j >= 0; j--) dp[i][j] = a[i] === b[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);

  const keepA = new Array<boolean>(n).fill(false);
  const keepB = new Array<boolean>(m).fill(false);
  let common = 0;
  for (let i = 0, j = 0; i < n && j < m; ) {
    if (a[i] === b[j]) {
      keepA[i++] = true;
      keepB[j++] = true;
      common += a[i - 1].trim().length;
    } else if (dp[i + 1][j] >= dp[i][j + 1]) i++;
    else j++;
  }

  // If little survives, highlighting fragments is just noise.
  const solid = (s: string) => s.replace(/\s+/g, '').length;
  const longest = Math.max(solid(oldText), solid(newText));
  if (longest > 0 && common / longest < 0.4) return null;

  return { old: toRanges(a, keepA), new: toRanges(b, keepB) };
}

function toRanges(tokens: string[], keep: boolean[]): Range[] {
  const ranges: Range[] = [];
  let pos = 0;
  for (let i = 0; i < tokens.length; i++) {
    const t = tokens[i];
    if (!keep[i]) {
      const last = ranges[ranges.length - 1];
      // Join with the previous change when only whitespace separates them.
      if (last && (last[1] === pos || tokens.slice(0, i).join('').slice(last[1]).trim() === '')) last[1] = pos + t.length;
      else ranges.push([pos, pos + t.length]);
    }
    pos += t.length;
  }
  // Don't highlight pure-whitespace edges.
  return ranges.filter(([s, e]) => e > s);
}

/**
 * Wraps the given text ranges of an HTML fragment (e.g. syntax-highlighted code) in
 * `<span class="cls">`, closing and reopening around tags so the markup stays well-formed.
 * Ranges are in plain-text characters; HTML entities count as one character.
 */
export function markRanges(html: string, ranges: Range[], cls: string): string {
  if (ranges.length === 0) return html;
  const open = `<span class="${cls}">`;
  let out = '';
  let textPos = 0;
  let r = 0;
  let inside = false;
  let i = 0;
  while (i < html.length) {
    while (r < ranges.length && textPos >= ranges[r][1]) {
      if (inside) {
        out += '</span>';
        inside = false;
      }
      r++;
    }
    if (!inside && r < ranges.length && textPos >= ranges[r][0] && textPos < ranges[r][1]) {
      out += open;
      inside = true;
    }
    const ch = html[i];
    if (ch === '<') {
      const end = html.indexOf('>', i);
      const tag = html.slice(i, end + 1);
      out += inside ? `</span>${tag}${open}` : tag;
      i = end + 1;
      continue;
    }
    if (ch === '&') {
      const end = html.indexOf(';', i);
      const entity = end > i && end - i < 10 ? html.slice(i, end + 1) : ch;
      out += entity;
      i += entity.length;
    } else {
      out += ch;
      i++;
    }
    textPos++;
  }
  if (inside) out += '</span>';
  return out.replace(new RegExp(`${open.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}</span>`, 'g'), '');
}
