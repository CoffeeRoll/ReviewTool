import { describe, expect, it } from 'vitest';
import { highlightFile, languageFor, setLanguageOverrides } from './highlight';

describe('languageFor', () => {
  it('maps file names and extensions to languages', () => {
    expect(languageFor('src/Program.cs')).toBe('cs');
    expect(languageFor('Views/Home/Index.cshtml')).toBe('razor');
    expect(languageFor('ReviewTool.Server.csproj')).toBe('xml');
    expect(languageFor('native/thing.h')).toBe('cpp');
    expect(languageFor('build/Dockerfile')).toBe('dockerfile');
    expect(languageFor('LICENSE')).toBeNull();
  });

  it('honours configured overrides', () => {
    setLanguageOverrides({ '.props': 'msbuild-ish', 'Directory.Packages.props': 'xml', tpl: 'html' });
    expect(languageFor('a/Directory.Packages.props')).toBe('xml');
    expect(languageFor('other.props')).toBe('msbuild-ish');
    expect(languageFor('page.tpl')).toBe('html');
    setLanguageOverrides({});
    expect(languageFor('other.props')).toBe('xml');
  });
});

describe('highlightFile', () => {
  it('returns per-line HTML that lines up with the text, CRLF included', async () => {
    const file = (await highlightFile('/*\r\n * if not code\r\n */\r\nif (x) { }\r\n', 'cs'))!;
    expect(file.lines).toEqual(['/*', ' * if not code', ' */', 'if (x) { }']);
    expect(file.html[1]).toContain('tk-comment');
    expect(file.html[3]).toContain('tk-control');
  });

  it('gives up on unknown languages and huge files', async () => {
    expect(await highlightFile('x', null)).toBeNull();
    expect(await highlightFile('x', 'no-such-language')).toBeNull();
    expect(await highlightFile('x\n'.repeat(300_000), 'cs')).toBeNull();
  });
});
