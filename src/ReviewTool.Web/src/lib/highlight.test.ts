import { describe, expect, it } from 'vitest';
import { highlightFile, highlightLine, splitHighlightedHtml } from './highlight';

describe('splitHighlightedHtml', () => {
  it('closes and reopens spans that cross line breaks', () => {
    const lines = splitHighlightedHtml('<span class="c">/* a\nb */</span> x\ny');
    expect(lines).toEqual(['<span class="c">/* a</span>', '<span class="c">b */</span> x', 'y']);
  });

  it('handles nested spans', () => {
    const lines = splitHighlightedHtml('<span class="a"><span class="b">1\n2</span>3\n4</span>');
    expect(lines).toEqual(['<span class="a"><span class="b">1</span></span>', '<span class="a"><span class="b">2</span>3</span>', '<span class="a">4</span>']);
  });
});

describe('highlightFile', () => {
  const source = ['/*', ' * if this were code, "if" would be a keyword', ' */', 'if (x) { return; }', ''].join('\r\n');

  it('keeps every line of a block comment coloured as a comment', () => {
    const file = highlightFile(source, 'csharp')!;
    expect(file.lines).toHaveLength(4);
    expect(file.lines[1]).toBe(' * if this were code, "if" would be a keyword');
    expect(file.html[1]).toContain('hljs-comment');
    expect(file.html[1]).not.toContain('hljs-keyword');
    expect(file.html[3]).toContain('hljs-keyword');
  });

  it('fixes what per-line highlighting gets wrong', () => {
    // On its own the middle line looks like code.
    expect(highlightLine(' * if this were code, "if" would be a keyword', 'csharp')).toContain('hljs-keyword');
  });

  it('gives up on unknown languages and huge files', () => {
    expect(highlightFile('x', null)).toBeNull();
    expect(highlightFile('x\n'.repeat(300_000), 'csharp')).toBeNull();
  });
});
