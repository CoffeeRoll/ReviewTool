/**
 * Syntax highlighting for the diff views. Whole files are tokenized with Shiki (VS Code's TextMate
 * grammars) in a Web Worker; each line comes back as self-contained HTML using `tk-<category>` classes
 * (see syntaxTheme.ts). Lines are shown as plain text until their file's highlighting arrives.
 */

/** Above this size a file is shown without syntax colours (tokenizing it would take too long). */
export const MAX_FILE_HIGHLIGHT_CHARS = 400_000;

export interface HighlightedFile {
  /** The file's lines as plain text (no line endings), for checking a diff line matches. */
  lines: string[];
  /** The same lines as highlighted HTML, each self-contained. */
  html: string[];
}

// ---- Language detection ----

/** Extensions that aren't Shiki language names or aliases already ("cs", "ts", "py", ... work as-is). */
const EXTENSION_LANGS: Record<string, string> = {
  cshtml: 'razor',
  csproj: 'xml',
  fsproj: 'xml',
  vbproj: 'xml',
  props: 'xml',
  targets: 'xml',
  slnx: 'xml',
  nuspec: 'xml',
  config: 'xml',
  xaml: 'xml',
  axaml: 'xml',
  resx: 'xml',
  manifest: 'xml',
  plist: 'xml',
  svg: 'xml',
  h: 'cpp',
  hh: 'cpp',
  hpp: 'cpp',
  hxx: 'cpp',
  cc: 'cpp',
  cxx: 'cpp',
  mjs: 'javascript',
  cjs: 'javascript',
  mts: 'typescript',
  cts: 'typescript',
  psm1: 'powershell',
  psd1: 'powershell',
  zsh: 'shellscript',
  editorconfig: 'ini',
  gradle: 'groovy',
  tfvars: 'hcl',
};

const FILENAME_LANGS: Record<string, string> = {
  dockerfile: 'dockerfile',
  containerfile: 'dockerfile',
  makefile: 'makefile',
  'cmakelists.txt': 'cmake',
  jenkinsfile: 'groovy',
  gemfile: 'ruby',
  rakefile: 'ruby',
  '.editorconfig': 'ini',
  '.gitattributes': 'ini',
};

let overrides: Record<string, string> = {};

/**
 * Extra mappings from configuration, e.g. { ".props": "xml", "Directory.Packages.props": "xml" }.
 * Keys are file names or extensions (with or without the dot); values are Shiki language ids.
 */
export function setLanguageOverrides(map: Record<string, string> | null | undefined) {
  overrides = {};
  for (const [k, v] of Object.entries(map ?? {})) if (v) overrides[k.toLowerCase().replace(/^\.(?=[^.]+$)/, '')] = v;
}

/**
 * The language to highlight a file as: configured override, known file name, extension mapping, or the
 * extension itself (Shiki recognises most extensions as aliases). Unknown ones fall back to plain text.
 */
export function languageFor(path: string): string | null {
  const name = path.slice(path.lastIndexOf('/') + 1).toLowerCase();
  const dot = name.lastIndexOf('.');
  const ext = dot > 0 ? name.slice(dot + 1) : dot === 0 ? name.slice(1) : '';
  return overrides[name] ?? (ext ? overrides[ext] : undefined) ?? FILENAME_LANGS[name] ?? (ext ? (EXTENSION_LANGS[ext] ?? ext) : null);
}

// ---- Highlighting ----

export function escapeHtml(s: string) {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

type Highlight = (text: string, lang: string) => Promise<string[] | null>;

let worker: Worker | null = null;
let workerFailed = false;
let nextId = 1;
const pending = new Map<number, (html: string[] | null) => void>();

/** Highlights in the Web Worker. Without one (only in tests), highlights in-process. */
const runHighlight: Highlight = async (text, lang) => {
  if (typeof Worker === 'undefined' || workerFailed) {
    // Kept out of production builds: importing the highlighter here would bundle every grammar twice.
    if (import.meta.env.VITEST) return (await import('./syntax')).highlightToLines(text, lang);
    return null; // plain text
  }
  if (!worker) {
    try {
      worker = new Worker(new URL('./highlight.worker.ts', import.meta.url), { type: 'module' });
      worker.onmessage = (e: MessageEvent<{ id: number; html: string[] | null }>) => {
        pending.get(e.data.id)?.(e.data.html);
        pending.delete(e.data.id);
      };
      worker.onerror = () => {
        // Couldn't start the worker: highlight on the main thread from now on.
        workerFailed = true;
        worker = null;
        for (const resolve of pending.values()) resolve(null);
        pending.clear();
      };
    } catch {
      workerFailed = true;
      return null;
    }
  }
  const id = nextId++;
  return new Promise((resolve) => {
    pending.set(id, resolve);
    worker!.postMessage({ id, text, lang });
  });
};

const fileCache = new Map<string, Promise<HighlightedFile | null>>();

/**
 * Highlights a whole file at once, so constructs spanning lines (block comments, multi-line strings)
 * are coloured correctly. Resolves to null for unsupported languages and very large files.
 */
export function highlightFile(text: string, lang: string | null, cacheKey?: string): Promise<HighlightedFile | null> {
  if (!lang || text.length > MAX_FILE_HIGHLIGHT_CHARS) return Promise.resolve(null);
  const key = cacheKey ? `${lang}\u0000${cacheKey}` : null;
  const hit = key ? fileCache.get(key) : undefined;
  if (hit) return hit;

  // Only CRLF is a line break to git; a lone CR stays part of its line.
  const normalized = text.replace(/\r\n/g, '\n');
  const body = normalized.endsWith('\n') ? normalized.slice(0, -1) : normalized;
  const lines = body.split('\n');
  const result = runHighlight(body, lang).then(
    (html) => (html && html.length === lines.length ? { lines, html } : null),
    () => null,
  );
  if (key) {
    if (fileCache.size > 60) fileCache.delete(fileCache.keys().next().value!);
    fileCache.set(key, result);
  }
  return result;
}
