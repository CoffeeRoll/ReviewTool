import { useEffect, useState } from 'react';
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

function readTheme(): Theme {
  try {
    return (localStorage.getItem(THEME_KEY) as Theme) || 'system';
  } catch {
    return 'system';
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
  const next: Record<Theme, Theme> = { system: 'light', light: 'dark', dark: 'system' };
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
