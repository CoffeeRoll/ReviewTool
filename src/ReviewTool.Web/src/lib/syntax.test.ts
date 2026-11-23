import { describe, expect, it } from 'vitest';
import { highlightToLines, isKnownLanguage } from './syntax';

/** Text of each token tagged with a category, e.g. classesOf(html, 'function') → ['Where', 'Sum']. */
function tagged(lines: string[], category: string): string[] {
  const re = new RegExp(`<span class="tk-${category}">([^<]*)</span>`, 'g');
  return lines.flatMap((l) => [...l.matchAll(re)].map((m) => m[1].trim())).filter(Boolean);
}

describe('highlightToLines', () => {
  it('colours C# function calls, types, variables and operators', async () => {
    const lines = (await highlightToLines(
      ['List<Order> items = repo.Load<Order>(id);', 'var total = items.Where(o => o.Amount >= limit).Sum(o => o.Amount);', 'if (total != 0 && count <= Max) { return; }'].join('\n'),
      'cs',
    ))!;
    expect(lines).toHaveLength(3);
    expect(tagged(lines, 'function')).toEqual(expect.arrayContaining(['Load', 'Where', 'Sum']));
    expect(tagged(lines, 'type')).toEqual(expect.arrayContaining(['List', 'Order']));
    expect(tagged(lines, 'operator')).toEqual(expect.arrayContaining(['&gt;=', '!=', '&amp;&amp;', '&lt;=']));
    // Generic angle brackets are type punctuation, not comparison operators.
    expect(tagged(lines, 'operator')).not.toContain('&lt;');
    expect(tagged(lines, 'control')).toEqual(expect.arrayContaining(['if', 'return']));
    expect([...tagged(lines, 'variable'), ...tagged(lines, 'parameter')].length).toBeGreaterThan(0);
  });

  it('works the same way for other languages', async () => {
    const ts = (await highlightToLines('const n = items.filter((x) => x.size > 3).map(render);', 'ts'))!;
    expect(tagged(ts, 'function')).toEqual(expect.arrayContaining(['filter', 'map']));
    const py = (await highlightToLines('class Repo(Base):\n    def load(self, key):\n        return len(key) >= 2', 'py'))!;
    expect(tagged(py, 'type')).toEqual(expect.arrayContaining(['Repo']));
    expect(tagged(py, 'function')).toEqual(expect.arrayContaining(['load', 'len']));
    const access = (await highlightToLines('total = line.qty * 2', 'py'))!;
    expect(tagged(access, 'property')).toContain('qty');
    expect(tagged(access, 'attribute')).toEqual([]);
    const go = (await highlightToLines('func (s *Server) Handle(w http.ResponseWriter) {}', 'go'))!;
    expect(tagged(go, 'type')).toEqual(expect.arrayContaining(['Server']));
  });

  it('colours brackets by depth, skipping brackets inside strings and comments', async () => {
    const [line] = (await highlightToLines('Run(items[Index("(x]")]); // (', 'cs'))!;
    const brackets = [...line.matchAll(/<span class="br br-(\d|bad)">(.)<\/span>/g)].map((m) => `${m[2]}${m[1]}`);
    expect(brackets).toEqual(['(0', '[1', '(2', ')2', ']1', ')0']);
  });

  it('keeps multi-line comments coloured on every line', async () => {
    const lines = (await highlightToLines('/*\n * if (x) return null;\n */\nif (x) { }', 'cs'))!;
    expect(lines[1]).toContain('tk-comment');
    expect(lines[1]).not.toContain('tk-control');
    expect(lines[3]).toContain('tk-control');
  });

  it('escapes HTML and returns null for unknown languages', async () => {
    const [line] = (await highlightToLines('var s = "<b>&";', 'cs'))!;
    expect(line).toContain('&lt;b&gt;&amp;');
    expect(line).not.toContain('<b>');
    expect(isKnownLanguage('cobolish')).toBe(false);
    expect(await highlightToLines('x', 'cobolish')).toBeNull();
  });
});
