import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { api } from '../lib/api';
import { shortSha } from '../lib/diff';
import { useSession } from '../lib/session';
import { PublishPanel } from './PublishPanel';
import { RANGE, REVIEW_STATUSES, type ReviewStatus } from '../lib/types';

export function Modal({ title, onClose, children, wide }: { title: string; onClose: () => void; children: ReactNode; wide?: boolean }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
  return (
    <div className="modal-backdrop" onMouseDown={onClose}>
      <div className={`modal ${wide ? 'wide' : ''}`} role="dialog" aria-label={title} onMouseDown={(e) => e.stopPropagation()} onKeyDown={(e) => e.stopPropagation()}>
        <div className="modal-head">
          <h3>{title}</h3>
          <button className="icon-btn" onClick={onClose} aria-label="Close">
            ×
          </button>
        </div>
        <div className="modal-body">{children}</div>
      </div>
    </div>
  );
}

/** Pick any file in the repository (at the current commit) to open in a pane. */
export function FileBrowser({ onClose }: { onClose: () => void }) {
  const { review, ui, openPane, files } = useSession();
  const [all, setAll] = useState<string[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [sel, setSel] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const changed = useMemo(() => new Set((files(ui.commitKey) ?? []).map((f) => f.path)), [files, ui.commitKey]);

  useEffect(() => {
    inputRef.current?.focus();
    api.tree(review.id, ui.commitKey).then(setAll, (e: Error) => setError(e.message));
  }, [review.id, ui.commitKey]);

  const results = useMemo(() => {
    if (!all) return [];
    const q = query.trim().toLowerCase();
    if (!q) return all.slice(0, 300);
    // Simple fuzzy match: all characters in order; prefer matches in the file name.
    const scored: { p: string; s: number }[] = [];
    for (const p of all) {
      const lower = p.toLowerCase();
      let i = 0;
      let score = 0;
      for (const ch of q) {
        const j = lower.indexOf(ch, i);
        if (j < 0) {
          score = -1;
          break;
        }
        score += j - i;
        i = j + 1;
      }
      if (score < 0) continue;
      if (lower.includes(q)) score -= 100;
      if (lower.slice(lower.lastIndexOf('/') + 1).includes(q)) score -= 100;
      scored.push({ p, s: score + p.length / 100 });
    }
    return scored.sort((a, b) => a.s - b.s).slice(0, 300).map((x) => x.p);
  }, [all, query]);

  const choose = (path: string, newPane: boolean) => {
    openPane({ path, mode: changed.has(path) ? 'diff' : 'file' }, { newPane });
    onClose();
  };

  return (
    <Modal title={`Open a file at ${ui.commitKey === RANGE ? 'head' : shortSha(ui.commitKey)}`} onClose={onClose}>
      <input
        ref={inputRef}
        className="input"
        placeholder="Type to search files…"
        value={query}
        onChange={(e) => {
          setQuery(e.target.value);
          setSel(0);
        }}
        onKeyDown={(e) => {
          if (e.key === 'ArrowDown') {
            e.preventDefault();
            setSel((s) => Math.min(s + 1, results.length - 1));
          } else if (e.key === 'ArrowUp') {
            e.preventDefault();
            setSel((s) => Math.max(s - 1, 0));
          } else if (e.key === 'Enter' && results[sel]) {
            choose(results[sel], e.ctrlKey || e.metaKey || e.shiftKey);
          }
        }}
      />
      <p className="muted small">Enter opens in the active pane, Ctrl/Shift+Enter opens a new pane. Changed files open as diffs.</p>
      {error && <div className="error">{error}</div>}
      {!all && !error && <div className="muted">Loading…</div>}
      <ul className="browser-list">
        {results.map((p, i) => (
          <li key={p}>
            <button className={i === sel ? 'sel' : ''} onMouseEnter={() => setSel(i)} onClick={(e) => choose(p, e.ctrlKey || e.metaKey || e.shiftKey)}>
              <span className="mono">{p}</span>
              {changed.has(p) && <span className="pill">changed</span>}
            </button>
          </li>
        ))}
      </ul>
    </Modal>
  );
}

export function FinishDialog({ onClose }: { onClose: () => void }) {
  const { review, patchReview } = useSession();
  const [summary, setSummary] = useState(review.summary ?? '');
  const [status, setStatus] = useState<ReviewStatus>(review.status);
  const [tab, setTab] = useState<'publish' | 'markdown'>('publish');
  const [includeResolved, setIncludeResolved] = useState(false);
  const [markdown, setMarkdown] = useState('');
  const [copied, setCopied] = useState(false);
  const [savedSummary, setSavedSummary] = useState(review.summary ?? '');

  // Save summary/status as they change, then regenerate the export.
  useEffect(() => {
    const t = window.setTimeout(async () => {
      if (summary !== review.summary || status !== review.status) await patchReview({ summary, status });
      setSavedSummary(summary);
      if (tab === 'markdown') setMarkdown(await api.exportMarkdown(review.id, includeResolved));
    }, 400);
    return () => window.clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [summary, status, includeResolved, tab]);

  const download = () => {
    const blob = new Blob([markdown], { type: 'text/markdown' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = `${review.name.replace(/[^\w.-]+/g, '-')}-review.md`;
    a.click();
    URL.revokeObjectURL(a.href);
  };

  return (
    <Modal title="Finish review" onClose={onClose} wide>
      <div className="form-grid">
        <label>
          Status
          <select className="input" value={status} onChange={(e) => setStatus(e.target.value as ReviewStatus)}>
            {REVIEW_STATUSES.map((s) => (
              <option key={s.value} value={s.value}>
                {s.label}
              </option>
            ))}
          </select>
        </label>
        <label className="span-2">
          Overall summary
          <textarea className="input" rows={3} value={summary} placeholder="High-level feedback for the author…" onChange={(e) => setSummary(e.target.value)} />
        </label>
      </div>
      <div className="seg tabs">
        <button className={tab === 'publish' ? 'on' : ''} onClick={() => setTab('publish')}>
          Post to GitHub
        </button>
        <button className={tab === 'markdown' ? 'on' : ''} onClick={() => setTab('markdown')}>
          Export Markdown
        </button>
      </div>
      {tab === 'publish' ? (
        <PublishPanel status={status} summary={savedSummary} />
      ) : (
        <>
          <div className="row">
            <label className="small">
              <input type="checkbox" checked={includeResolved} onChange={(e) => setIncludeResolved(e.target.checked)} /> include resolved notes
            </label>
            <span className="spacer" />
            <button
              className="btn"
              onClick={async () => {
                await navigator.clipboard?.writeText(markdown);
                setCopied(true);
                setTimeout(() => setCopied(false), 1500);
              }}
            >
              {copied ? 'Copied!' : 'Copy'}
            </button>
            <button className="btn" onClick={download}>
              Download .md
            </button>
          </div>
          <pre className="export-preview">{markdown || 'Generating…'}</pre>
        </>
      )}
    </Modal>
  );
}

const SHORTCUTS: [string, string][] = [
  ['n / ]', 'Next commit'],
  ['p / [', 'Previous commit'],
  ['j / k', 'Next / previous file'],
  ['v', 'Toggle current file reviewed'],
  ['Shift+M', 'Mark commit reviewed and go to next'],
  ['a', 'Jump to the combined "all changes" diff'],
  ['u', 'Toggle unified / split diff'],
  ['l', 'Toggle stacked / pane layout'],
  ['w', 'Toggle line wrapping'],
  ['i', 'Hide / show whitespace-only changes'],
  ['d', 'Dim lines a later commit rewrites (marked ↻)'],
  ['s', 'Changes since your last review (after a refresh)'],
  ['o', 'Open any file from the repo'],
  ['c', 'Add a note on the current commit'],
  ['Shift+N', 'Toggle notes panel'],
  ['?', 'Show this help'],
  ['Ctrl+Enter', 'Save the note being edited'],
  ['Esc', 'Cancel note / close dialog'],
];

export function ShortcutsDialog({ onClose }: { onClose: () => void }) {
  return (
    <Modal title="Keyboard shortcuts" onClose={onClose}>
      <table className="shortcuts">
        <tbody>
          {SHORTCUTS.map(([k, d]) => (
            <tr key={k}>
              <td>
                <kbd>{k}</kbd>
              </td>
              <td>{d}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="muted small">
        Click the <strong>+</strong> next to any line to add a note; shift-click another line's <strong>+</strong> to extend the selection to a range.
        Tick <strong>Hunk reviewed</strong> on a hunk header to check off part of a file. <strong>↻ n</strong> marks a line that commit n changes
        again, and <strong>⇢ moved</strong> marks code that moved rather than changed.
      </p>
    </Modal>
  );
}
