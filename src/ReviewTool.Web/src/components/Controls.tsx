import { useEffect, useRef, useState } from 'react';
import { useSession } from '../lib/session';
import type { DiffStyle, Layout } from '../lib/types';

export function LayoutToggle({ value, onToggle }: { value: Layout; onToggle: () => void }) {
  return (
    <div className="seg" title="Stacked: every file in the commit, one after another. Panes: several files side by side. (l)">
      <button className={value === 'stack' ? 'on' : ''} onClick={() => value !== 'stack' && onToggle()}>
        ☰ Stacked
      </button>
      <button className={value === 'panes' ? 'on' : ''} onClick={() => value !== 'panes' && onToggle()}>
        ◫ Panes
      </button>
    </div>
  );
}

export function DiffStyleToggle({ value, onChange }: { value: DiffStyle; onChange: (v: DiffStyle) => void }) {
  return (
    <div className="seg" title="Diff style (u)">
      <button className={value === 'unified' ? 'on' : ''} onClick={() => onChange('unified')}>
        Unified
      </button>
      <button className={value === 'split' ? 'on' : ''} onClick={() => onChange('split')}>
        Split
      </button>
    </div>
  );
}

type Theme = 'system' | 'light' | 'dark';
const THEME_KEY = 'reviewtool.theme';

/** Dark is the default; the toggle cycles dark → light → follow the OS. */
const DEFAULT_THEME: Theme = 'dark';

function readTheme(): Theme {
  try {
    return (localStorage.getItem(THEME_KEY) as Theme) || DEFAULT_THEME;
  } catch {
    return DEFAULT_THEME;
  }
}

export function applyStoredTheme() {
  const t = readTheme();
  if (t === 'system') document.documentElement.removeAttribute('data-theme');
  else document.documentElement.setAttribute('data-theme', t);
}

export function ThemeToggle() {
  const [theme, setTheme] = useState<Theme>(readTheme);
  useEffect(() => {
    try {
      localStorage.setItem(THEME_KEY, theme);
    } catch {
      /* storage unavailable */
    }
    applyStoredTheme();
  }, [theme]);
  const next: Record<Theme, Theme> = { dark: 'light', light: 'system', system: 'dark' };
  return (
    <button className="icon-btn theme-btn" title={`Theme: ${theme} (click to change)`} onClick={() => setTheme(next[theme])}>
      <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden fill="none" stroke="currentColor" strokeWidth="1.5">
        {theme === 'light' && (
          <>
            <circle cx="8" cy="8" r="3" />
            <path d="M8 1v2M8 13v2M1 8h2M13 8h2M3 3l1.4 1.4M11.6 11.6 13 13M3 13l1.4-1.4M11.6 4.4 13 3" strokeLinecap="round" />
          </>
        )}
        {theme === 'dark' && <path d="M13 9.5A5.5 5.5 0 1 1 6.5 3a4.5 4.5 0 0 0 6.5 6.5Z" strokeLinejoin="round" />}
        {theme === 'system' && (
          <>
            <circle cx="8" cy="8" r="6" />
            <path d="M8 2a6 6 0 0 1 0 12Z" fill="currentColor" />
          </>
        )}
      </svg>
    </button>
  );
}

/** Dropdown with display options that don't need to be on screen all the time. */
export function ViewMenu() {
  const { ui, setUi } = useSession();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => !ref.current?.contains(e.target as Node) && setOpen(false);
    window.addEventListener('mousedown', close);
    return () => window.removeEventListener('mousedown', close);
  }, [open]);
  const active = (ui.ignoreWhitespace ? 1 : 0) + (ui.wrap ? 1 : 0);
  const toggle = (label: string, key: 'wrap' | 'ignoreWhitespace' | 'showLater', hint: string, shortcut: string) => (
    <label className="menu-item" title={hint}>
      <input type="checkbox" checked={!!ui[key]} onChange={(e) => setUi({ [key]: e.target.checked })} />
      <span>{label}</span>
      <kbd>{shortcut}</kbd>
    </label>
  );
  return (
    <div className="menu" ref={ref}>
      <button className={`btn small ${open ? 'active' : ''}`} onClick={() => setOpen((o) => !o)} aria-expanded={open}>
        View{active > 0 ? ` (${active})` : ''} ▾
      </button>
      {open && (
        <div className="menu-pop" role="menu">
          <label className="menu-item">
            <span>Context lines</span>
            <select className="input small" value={ui.context} onChange={(e) => setUi({ context: Number(e.target.value) })}>
              {[3, 10, 25, 100].map((n) => (
                <option key={n} value={n}>
                  {n}
                </option>
              ))}
            </select>
          </label>
          {toggle('Hide whitespace changes', 'ignoreWhitespace', 'Ignore changes that only touch whitespace', 'i')}
          {toggle('Wrap long lines', 'wrap', 'Wrap instead of scrolling horizontally', 'w')}
          {toggle('Dim lines rewritten later', 'showLater', 'Fade added lines that a later commit changes again (they are always marked ↻)', 'd')}
          <div className="menu-note muted small">
            <span className="legend moved" /> moved code · <span className="legend wd" /> changed words · ↻ n changed again in commit n
          </div>
        </div>
      )}
    </div>
  );
}
