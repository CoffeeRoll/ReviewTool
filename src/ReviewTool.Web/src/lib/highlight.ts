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
