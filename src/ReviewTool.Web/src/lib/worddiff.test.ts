import { describe, expect, it } from 'vitest';
import { hunkRanges, isHunkReviewed, parseRanges, toRanges } from './ranges';
import type { DiffHunk } from './types';
import { markRanges, wordDiff } from './worddiff';

describe('wordDiff', () => {
  it('highlights only the changed words', () => {
    const d = wordDiff('return a / b;', 'return a / divisor;')!;
    expect(d.old).toEqual([[11, 12]]);
    expect(d.new).toEqual([[11, 18]]);
  });

  it('gives up on lines that are completely different', () => {
    expect(wordDiff('int x = 1;', 'Console.WriteLine("hello world");')).toBeNull();
  });

  it('joins changes separated only by whitespace', () => {
    const d = wordDiff('a b c d e f', 'a X Y d e f')!;
    expect(d.new).toEqual([[2, 5]]);
  });
});

describe('markRanges', () => {
  it('wraps plain text', () => {
    expect(markRanges('hello world', [[6, 11]], 'w')).toBe('hello <span class="w">world</span>');
  });

  it('closes and reopens around tags and counts entities as one char', () => {
    const html = '<span class="k">if</span> (a &lt; b)';
    expect(markRanges(html, [[0, 7]], 'w')).toBe('<span class="k"><span class="w">if</span></span><span class="w"> (a &lt;</span> b)');
  });
});

describe('line ranges', () => {
  it('round-trips', () => {
    expect(toRanges('N', [5, 3, 4, 9])).toEqual(['N3-5', 'N9']);
    const p = parseRanges(['N3-5', 'O2']);
    expect([...p.new]).toEqual([3, 4, 5]);
    expect(p.old.has(2)).toBe(true);
  });

  it('knows when a hunk is fully reviewed', () => {
    const hunk = {
      lines: [
        { type: 'Context', oldLine: 1, newLine: 1, text: '', noNewlineAtEnd: false },
        { type: 'Delete', oldLine: 2, newLine: null, text: '', noNewlineAtEnd: false },
        { type: 'Add', oldLine: null, newLine: 2, text: '', noNewlineAtEnd: false },
      ],
    } as DiffHunk;
    expect(hunkRanges(hunk)).toEqual(['O2', 'N2']);
    expect(isHunkReviewed(hunk, parseRanges(['O2']))).toBe(false);
    expect(isHunkReviewed(hunk, parseRanges(['O2', 'N2']))).toBe(true);
  });
});

import { buildTree } from './tree';
import type { ChangedFile } from './types';

describe('buildTree', () => {
  const f = (path: string) => ({ path }) as ChangedFile;
  it('nests folders, merges single-child chains, and lists folders first', () => {
    const tree = buildTree([f('src/app/a.ts'), f('src/app/b.ts'), f('README.md'), f('docs/guide/x.md')]);
    expect(tree.map((n) => n.name)).toEqual(['docs/guide', 'src/app', 'README.md']);
    expect(tree[1].children.map((n) => n.name)).toEqual(['a.ts', 'b.ts']);
  });
});
