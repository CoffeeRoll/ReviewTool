import hljs from 'highlight.js/lib/common';

const EXTRA_ALIASES: Record<string, string> = {
  cs: 'csharp',
  csx: 'csharp',
  razor: 'xml',
  cshtml: 'xml',
  csproj: 'xml',
  props: 'xml',
  targets: 'xml',
  slnx: 'xml',
  config: 'xml',
  xaml: 'xml',
  resx: 'xml',
  tsx: 'typescript',
  jsx: 'javascript',
  mjs: 'javascript',
  cjs: 'javascript',
  yml: 'yaml',
  md: 'markdown',
  ps1: 'powershell',
  psm1: 'powershell',
  sh: 'bash',
  zsh: 'bash',
  h: 'c',
  hpp: 'cpp',
  cc: 'cpp',
  rs: 'rust',
  kt: 'kotlin',
  vb: 'vbnet',
  fs: 'fsharp',
  svg: 'xml',
  vue: 'xml',
  toml: 'ini',
  editorconfig: 'ini',
  gitignore: 'bash',
  dockerfile: 'dockerfile',
};

const cache = new Map<string, string>();

export function languageFor(path: string): string | null {
  const name = path.slice(path.lastIndexOf('/') + 1).toLowerCase();
  if (name === 'dockerfile') return hljs.getLanguage('dockerfile') ? 'dockerfile' : null;
  if (name === 'makefile') return 'makefile';
  const dot = name.lastIndexOf('.');
  const ext = dot >= 0 ? name.slice(dot + 1) : name;
  const lang = EXTRA_ALIASES[ext] ?? ext;
  return hljs.getLanguage(lang) ? lang : null;
}

function escapeHtml(s: string) {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/** Highlights a single line. Returns HTML that is safe to inject (hljs escapes input). */
export function highlightLine(text: string, lang: string | null): string {
  if (!lang || text.length > 2000) return escapeHtml(text);
  const key = `${lang}\u0000${text}`;
  const hit = cache.get(key);
  if (hit !== undefined) return hit;
  let html: string;
  try {
    html = hljs.highlight(text, { language: lang, ignoreIllegals: true }).value;
  } catch {
    html = escapeHtml(text);
  }
  if (cache.size > 20000) cache.clear();
  cache.set(key, html);
  return html;
}

/** Above this size a file is highlighted line by line instead (fast, but loses multi-line context). */
export const MAX_FILE_HIGHLIGHT_CHARS = 400_000;

export interface HighlightedFile {
  /** The file's lines as plain text (no line endings), for checking a diff line matches. */
  lines: string[];
  /** The same lines as highlighted HTML, each self-contained (open spans closed and reopened). */
  html: string[];
}

const fileCache = new Map<string, HighlightedFile>();

/**
 * Highlights a whole file at once, so constructs spanning lines (block comments, multi-line strings,
 * doc comments) are coloured correctly, then splits the result into lines. Returns null for unknown
 * languages and very large files.
 */
export function highlightFile(text: string, lang: string | null, cacheKey?: string): HighlightedFile | null {
  if (!lang || text.length > MAX_FILE_HIGHLIGHT_CHARS) return null;
  const key = cacheKey ? `${lang}\u0000${cacheKey}` : null;
  const hit = key ? fileCache.get(key) : undefined;
  if (hit) return hit;

  // Only CRLF is a line break to git; a lone CR stays part of its line.
  const normalized = text.replace(/\r\n/g, '\n');
  const body = normalized.endsWith('\n') ? normalized.slice(0, -1) : normalized;
  let highlighted: string;
  try {
    highlighted = hljs.highlight(body, { language: lang, ignoreIllegals: true }).value;
  } catch {
    return null;
  }
  const result = { lines: body.split('\n'), html: splitHighlightedHtml(highlighted) };
  if (result.html.length !== result.lines.length) return null; // shouldn't happen; fall back to per-line

  if (key) {
    if (fileCache.size > 60) fileCache.delete(fileCache.keys().next().value!);
    fileCache.set(key, result);
  }
  return result;
}

const TOKEN = /(<span[^>]*>)|(<\/span>)|(\n)|([^<\n]+)/g;

/**
 * Splits highlight.js output into one HTML string per line. A span that is open at a line break (e.g. a
 * block comment) is closed at the end of that line and reopened at the start of the next, so each line
 * renders on its own with the right colours.
 */
export function splitHighlightedHtml(html: string): string[] {
  const lines: string[] = [];
  const open: string[] = [];
  let current = '';
  for (const m of html.matchAll(TOKEN)) {
    if (m[1]) {
      open.push(m[1]);
      current += m[1];
    } else if (m[2]) {
      open.pop();
      current += m[2];
    } else if (m[3]) {
      lines.push(current + '</span>'.repeat(open.length));
      current = open.join('');
    } else {
      current += m[4];
    }
  }
  lines.push(current + '</span>'.repeat(open.length));
  return lines;
}
