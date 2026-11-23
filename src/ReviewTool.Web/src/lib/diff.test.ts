import { describe, expect, it } from 'vitest';
import { anchorFor, commitProgress, hiddenLinesBefore, linesText, noteAnchorKey, toSplitRows } from './diff';
import type { DiffHunk, DiffLine, FileDiff, ReviewNote } from './types';

const ctx = (o: number, n: number, text = 'c'): DiffLine => ({ type: 'Context', oldLine: o, newLine: n, text, noNewlineAtEnd: false });
const del = (o: number, text = 'd'): DiffLine => ({ type: 'Delete', oldLine: o, newLine: null, text, noNewlineAtEnd: false });
const add = (n: number, text = 'a'): DiffLine => ({ type: 'Add', oldLine: null, newLine: n, text, noNewlineAtEnd: false });

describe('toSplitRows', () => {
  it('pairs deletions with following additions', () => {
    const rows = toSplitRows([ctx(1, 1), del(2), del(3), add(2), ctx(4, 3)]);
    expect(rows).toHaveLength(4);
    expect(rows[1]).toEqual({ left: del(2), right: add(2) });
    expect(rows[2]).toEqual({ left: del(3), right: null });
    expect(rows[3].left).toBe(rows[3].right);
  });

  it('handles pure additions', () => {
    const rows = toSplitRows([add(1), add(2)]);
    expect(rows.every((r) => r.left === null)).toBe(true);
  });
});

describe('anchors', () => {
  it('uses the old side for deletions and new side otherwise', () => {
    expect(anchorFor(del(7))).toEqual({ side: 'Old', line: 7 });
    expect(anchorFor(add(9))).toEqual({ side: 'New', line: 9 });
    expect(anchorFor(ctx(3, 5))).toEqual({ side: 'New', line: 5 });
  });

  it('anchors multi-line notes to their last line', () => {
    const note = { side: 'New', line: 3, endLine: 6 } as ReviewNote;
    expect(noteAnchorKey(note)).toBe('New:6');
  });
});

describe('hiddenLinesBefore', () => {
  const hunks = [
    { oldStart: 5, oldCount: 3, newStart: 5, newCount: 4, header: '', lines: [] },
    { oldStart: 20, oldCount: 2, newStart: 21, newCount: 2, header: '', lines: [] },
  ] as DiffHunk[];
  it('counts lines between hunks', () => {
    expect(hiddenLinesBefore(hunks, 0)).toBe(4);
    expect(hiddenLinesBefore(hunks, 1)).toBe(12);
  });
});

describe('linesText', () => {
  it('extracts a range from one side', () => {
    const diff = { hunks: [{ lines: [ctx(1, 1, 'one'), del(2, 'two'), add(2, 'TWO'), ctx(3, 3, 'three')] }] } as unknown as FileDiff;
    expect(linesText(diff, 'New', 2, 3)).toBe('TWO\nthree');
    expect(linesText(diff, 'Old', 1, 2)).toBe('one\ntwo');
  });
});

describe('commitProgress', () => {
  it('caps at the file count', () => {
    expect(commitProgress({ a: ['x', 'y', 'z'] }, 'a', 2)).toEqual({ reviewed: 2, total: 2, done: true });
    expect(commitProgress({}, 'a', 2).done).toBe(false);
    expect(commitProgress({}, 'a', 0).done).toBe(true);
  });
});

import { trimToChange } from './diff';

describe('trimToChange', () => {
  // A hunk with two separate change blocks far apart; the line of interest is old line 30.
  const lines: DiffLine[] = [];
  let o = 1;
  let n = 1;
  for (let i = 0; i < 20; i++) lines.push(ctx(o++, n++));
  lines.push(del(o++), add(n++)); // first block (old 21)
  for (let i = 0; i < 8; i++) lines.push(ctx(o++, n++));
  lines.push(del(o++), del(o++), add(n++)); // second block (old 30, 31)
  for (let i = 0; i < 20; i++) lines.push(ctx(o++, n++));
  const hunk = { oldStart: 1, oldCount: o - 1, newStart: 1, newCount: n - 1, header: '', lines } as DiffHunk;

  it('keeps only the change block touching the line, plus context', () => {
    const { hunk: t, trimmed } = trimToChange(hunk, 30);
    expect(trimmed).toBe(true);
    expect(t.lines.map((l) => l.type[0]).join('')).toBe('CCCDDACCC');
    expect(t.oldStart).toBe(27);
    expect(t.oldCount).toBe(8);
    expect(t.newCount).toBe(7);
  });

  it('leaves small hunks alone', () => {
    const small = { ...hunk, lines: [ctx(1, 1), del(2), add(2), ctx(3, 3)] } as DiffHunk;
    expect(trimToChange(small, 2).trimmed).toBe(false);
  });
});
