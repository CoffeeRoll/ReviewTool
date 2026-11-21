import type { DiffHunk } from './types';

/** Encodes changed-line sets like the server: "N12-20" (new side) / "O5" (old side). */
export function parseRanges(ranges: string[] | undefined | null): { old: Set<number>; new: Set<number> } {
  const result = { old: new Set<number>(), new: new Set<number>() };
  for (const r of ranges ?? []) {
    const m = /^([NO])(\d+)(?:-(\d+))?$/.exec(r);
    if (!m) continue;
    const target = m[1] === 'O' ? result.old : result.new;
    const from = Number(m[2]);
    const to = m[3] ? Number(m[3]) : from;
    for (let i = from; i <= to; i++) target.add(i);
  }
  return result;
}

export function toRanges(side: 'N' | 'O', lines: number[]): string[] {
  const sorted = [...new Set(lines)].sort((a, b) => a - b);
  const out: string[] = [];
  let start: number | null = null;
  let prev = 0;
  for (const n of sorted) {
    if (start === null) start = n;
    else if (n !== prev + 1) {
      out.push(start === prev ? `${side}${start}` : `${side}${start}-${prev}`);
      start = n;
    }
    prev = n;
  }
  if (start !== null) out.push(start === prev ? `${side}${start}` : `${side}${start}-${prev}`);
  return out;
}

/** The changed (added/deleted) lines of a hunk, as range strings. */
export function hunkRanges(hunk: DiffHunk): string[] {
  const adds: number[] = [];
  const dels: number[] = [];
  for (const l of hunk.lines) {
    if (l.type === 'Add') adds.push(l.newLine!);
    else if (l.type === 'Delete') dels.push(l.oldLine!);
  }
  return [...toRanges('O', dels), ...toRanges('N', adds)];
}

export function isHunkReviewed(hunk: DiffHunk, reviewed: { old: Set<number>; new: Set<number> }): boolean {
  let changed = 0;
  for (const l of hunk.lines) {
    if (l.type === 'Add') {
      changed++;
      if (!reviewed.new.has(l.newLine!)) return false;
    } else if (l.type === 'Delete') {
      changed++;
      if (!reviewed.old.has(l.oldLine!)) return false;
    }
  }
  return changed > 0;
}
