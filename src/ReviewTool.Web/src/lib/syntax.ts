import { createHighlighterCore, type HighlighterCore, type ThemedToken } from 'shiki/core';
import { createJavaScriptRegexEngine } from 'shiki/engine/javascript';
import { bundledLanguages } from 'shiki/langs';
import { COLOR_TO_CATEGORY, TEXTUAL, reviewToolTheme, type Category } from './syntaxTheme';

/**
 * Tokenizes source with Shiki (VS Code's TextMate grammars) and renders one self-contained HTML string
 * per line, using `tk-<category>` classes and depth-coloured brackets (`br br-0..2`).
 * Runs in a Web Worker in the app; it has no DOM dependencies, so tests can call it directly.
 */

let highlighter: Promise<HighlighterCore> | null = null;
const loading = new Map<string, Promise<boolean>>();

function getHighlighter() {
  // The JavaScript regex engine avoids shipping WebAssembly; "forgiving" skips the rare pattern it can't run.
  highlighter ??= createHighlighterCore({ themes: [reviewToolTheme], langs: [], engine: createJavaScriptRegexEngine({ forgiving: true }) });
  return highlighter;
}

/** True when Shiki has a grammar for this name or alias (e.g. "cs", "csharp", "razor"). */
export function isKnownLanguage(lang: string): boolean {
  return Object.prototype.hasOwnProperty.call(bundledLanguages, lang);
}

async function ensureLanguage(lang: string): Promise<boolean> {
  if (!isKnownLanguage(lang)) return false;
  let p = loading.get(lang);
  if (!p) {
    p = getHighlighter()
      .then(async (h) => {
        await h.loadLanguage(bundledLanguages[lang as keyof typeof bundledLanguages]);
        return true;
      })
      .catch(() => false);
    loading.set(lang, p);
  }
  return p;
}

const OPEN = '([{';
const CLOSE = ')]}';
const escapeHtml = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

/** Highlights a whole file. Returns null if the language isn't supported. Line endings must already be "\n". */
export async function highlightToLines(text: string, lang: string): Promise<string[] | null> {
  if (!(await ensureLanguage(lang))) return null;
  const h = await getHighlighter();
  const { tokens } = h.codeToTokens(text, { lang, theme: 'reviewtool' });
  return renderLines(tokens);
}

/** Turns themed tokens into HTML lines; brackets outside strings/comments are coloured by nesting depth. */
export function renderLines(tokens: ThemedToken[][]): string[] {
  const stack: string[] = [];
  return tokens.map((line) => {
    let html = '';
    for (const token of line) {
      const category: Category | undefined = token.color ? COLOR_TO_CATEGORY.get(token.color.toLowerCase()) : undefined;
      const cls = category ? `tk-${category}` : '';
      if (category && TEXTUAL.has(category)) {
        html += wrap(cls, escapeHtml(token.content));
        continue;
      }
      // Split out bracket characters so each gets its depth colour.
      let run = '';
      const flush = () => {
        if (run) html += wrap(cls, escapeHtml(run));
        run = '';
      };
      for (const ch of token.content) {
        const open = OPEN.indexOf(ch);
        const close = CLOSE.indexOf(ch);
        if (open < 0 && close < 0) {
          run += ch;
          continue;
        }
        flush();
        if (open >= 0) {
          html += `<span class="br br-${stack.length % 3}">${ch}</span>`;
          stack.push(CLOSE[open]);
        } else if (stack.length && stack[stack.length - 1] === ch) {
          stack.pop();
          html += `<span class="br br-${stack.length % 3}">${ch}</span>`;
        } else {
          html += `<span class="br br-bad">${ch}</span>`; // unmatched
        }
      }
      flush();
    }
    return html;
  });
}

const wrap = (cls: string, html: string) => (cls ? `<span class="${cls}">${html}</span>` : html);
