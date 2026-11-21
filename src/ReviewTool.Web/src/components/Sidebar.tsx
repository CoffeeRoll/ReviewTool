import { useEffect, useMemo, useRef, useState } from 'react';
import { basename, commitProgress, dirname, shortSha } from '../lib/diff';
import { useSession } from '../lib/session';
import { RANGE, type ChangedFile, type ReviewCommit } from '../lib/types';
import { StatusBadge } from './FileViews';

export function ProgressRing({ value, total, size = 16 }: { value: number; total: number; size?: number }) {
  const done = total === 0 || value >= total;
  const r = size / 2 - 2;
  const c = 2 * Math.PI * r;
  const frac = total === 0 ? 1 : value / total;
  return (
    <svg className={`ring ${done ? 'done' : value > 0 ? 'partial' : ''}`} width={size} height={size} viewBox={`0 0 ${size} ${size}`} aria-hidden>
      <circle cx={size / 2} cy={size / 2} r={r} className="ring-bg" />
      {done ? (
        <>
          <circle cx={size / 2} cy={size / 2} r={r} className="ring-fill-solid" />
          <path d={`M${size * 0.3} ${size * 0.52} L${size * 0.45} ${size * 0.66} L${size * 0.72} ${size * 0.36}`} className="ring-check" />
        </>
      ) : (
        <circle
          cx={size / 2}
          cy={size / 2}
          r={r}
          className="ring-fill"
          strokeDasharray={`${c * frac} ${c}`}
          transform={`rotate(-90 ${size / 2} ${size / 2})`}
        />
      )}
    </svg>
  );
}

export function Sidebar({ onBrowse }: { onBrowse: () => void }) {
  const { review, ui, setUi } = useSession();
  const width = ui.sidebarWidth;
  const dragging = useRef(false);

  useEffect(() => {
    const move = (e: MouseEvent) => {
      if (!dragging.current) return;
      setUi({ sidebarWidth: Math.min(640, Math.max(220, e.clientX)) });
    };
    const up = () => {
      dragging.current = false;
      document.body.classList.remove('resizing');
    };
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', up);
    return () => {
      window.removeEventListener('mousemove', move);
      window.removeEventListener('mouseup', up);
    };
  }, [setUi]);

  return (
    <aside className="sidebar" style={{ width }}>
      <CommitList commits={review.commits} />
      <FileList onBrowse={onBrowse} />
      <div
        className="resize-handle"
        onMouseDown={(e) => {
          e.preventDefault();
          dragging.current = true;
          document.body.classList.add('resizing');
        }}
      />
    </aside>
  );
}

function CommitList({ commits }: { commits: ReviewCommit[] }) {
  const { review, ui, selectCommit, files, loadFiles } = useSession();
  const active = commits.filter((c) => !c.outdated);
  const outdated = commits.filter((c) => c.outdated);
  const rangeTotal = files(RANGE)?.length;
  const rangeReviewed = Math.min(review.reviewedFiles[RANGE]?.length ?? 0, rangeTotal ?? 0);
  const listRef = useRef<HTMLOListElement>(null);

  useEffect(() => {
    loadFiles(RANGE).catch(() => undefined);
  }, [loadFiles]);

  useEffect(() => {
    listRef.current?.querySelector('.commit.selected')?.scrollIntoView({ block: 'nearest' });
  }, [ui.commitKey]);

  const item = (c: ReviewCommit, index: number | null) => {
    const p = commitProgress(review.reviewedFiles, c.sha, c.fileCount);
    const notes = review.notes.filter((n) => n.commitSha === c.sha).length;
    return (
      <li key={c.sha}>
        <button className={`commit ${ui.commitKey === c.sha ? 'selected' : ''} ${p.done ? 'done' : ''}`} onClick={() => selectCommit(c.sha)}>
          <span className="commit-index">{index ?? '–'}</span>
          <ProgressRing value={p.reviewed} total={p.total} />
          <span className="commit-main">
            <span className="commit-subject">{c.subject || '(no message)'}</span>
            <span className="commit-meta muted">
              <span className="mono">{shortSha(c.sha)}</span> · {c.authorName}
              {c.isMerge && <span className="pill">merge</span>}
            </span>
          </span>
          <span className="commit-side">
            <span className="stat small">
              <span className="add">+{c.additions}</span> <span className="del">−{c.deletions}</span>
            </span>
            {notes > 0 && <span className="pill">💬 {notes}</span>}
          </span>
        </button>
      </li>
    );
  };

  return (
    <div className="sidebar-section commits">
      <div className="section-title">
        Commits <span className="muted">({active.length})</span>
      </div>
      <ol className="commit-list" ref={listRef}>
        <li>
          <button className={`commit range ${ui.commitKey === RANGE ? 'selected' : ''}`} onClick={() => selectCommit(RANGE)}>
            <span className="commit-index">∑</span>
            <ProgressRing value={rangeReviewed} total={rangeTotal ?? 1} />
            <span className="commit-main">
              <span className="commit-subject">All changes (combined diff)</span>
              <span className="commit-meta muted mono">
                {shortSha(review.mergeBaseSha)}..{shortSha(review.headSha)}
              </span>
            </span>
          </button>
        </li>
        {active.map((c, i) => item(c, i + 1))}
        {outdated.length > 0 && <li className="list-divider muted small">No longer in range (kept for notes)</li>}
        {outdated.map((c) => item(c, null))}
      </ol>
    </div>
  );
}

function FileList({ onBrowse }: { onBrowse: () => void }) {
  const { review, ui, setUi, files, loadFiles, isReviewed, setReviewed, openPane, setError } = useSession();
  const [filter, setFilter] = useState('');
  const key = ui.commitKey;
  const list = files(key);

  useEffect(() => {
    loadFiles(key).catch((e: Error) => setError(e.message));
  }, [key, loadFiles, setError]);

  const shown = useMemo(() => {
    const f = filter.trim().toLowerCase();
    return (list ?? []).filter((x) => !f || x.path.toLowerCase().includes(f));
  }, [list, filter]);

  const noteCounts = useMemo(() => {
    const m = new Map<string, number>();
    for (const n of review.notes) if (n.commitSha === key && n.filePath) m.set(n.filePath, (m.get(n.filePath) ?? 0) + 1);
    return m;
  }, [review.notes, key]);

  const reviewedCount = (list ?? []).filter((f) => isReviewed(key, f.path)).length;
  const allReviewed = !!list && list.length > 0 && reviewedCount === list.length;

  const open = (f: ChangedFile, newPane: boolean) => {
    if (ui.layout === 'panes' || newPane) openPane({ path: f.path, oldPath: f.oldPath, mode: 'diff' }, { newPane });
    else setUi({ currentFile: f.path });
    // The stacked layout scrolls to the file card.
    requestAnimationFrame(() => {
      if (ui.layout === 'stack' && !newPane) {
        document.querySelector(`.file-card[data-path="${CSS.escape(f.path)}"]`)?.scrollIntoView({ block: 'start' });
      }
    });
  };

  return (
    <div className="sidebar-section files">
      <div className="section-title">
        Files{' '}
        <span className="muted">
          {list ? `${reviewedCount}/${list.length} reviewed` : '…'}
        </span>
        <span className="spacer" />
        <button className="btn-link" title="Open any file from the repository at this commit (o)" onClick={onBrowse}>
          Browse…
        </button>
      </div>
      <div className="file-tools">
        <input className="input small" placeholder="Filter files…" value={filter} onChange={(e) => setFilter(e.target.value)} />
        {list && list.length > 0 && (
          <button
            className="btn small"
            title="Mark every file in this commit as reviewed"
            onClick={() => setReviewed(key, list.map((f) => f.path), !allReviewed)}
          >
            {allReviewed ? 'Unmark all' : 'Mark all'}
          </button>
        )}
      </div>
      <ul className="file-list">
        {!list && <li className="muted small pad">Loading…</li>}
        {list && list.length === 0 && <li className="muted small pad">This commit has no file changes.</li>}
        {shown.map((f) => {
          const reviewed = isReviewed(key, f.path);
          const notes = noteCounts.get(f.path);
          const openInPane = ui.layout === 'panes' && ui.panes.some((p) => p.path === f.path);
          return (
            <li key={f.path}>
              <div
                className={`file-item ${ui.currentFile === f.path ? 'current' : ''} ${reviewed ? 'reviewed' : ''}`}
                onClick={(e) => open(f, e.ctrlKey || e.metaKey || e.shiftKey)}
                title={`${f.oldPath ? `${f.oldPath} → ` : ''}${f.path}\nCtrl/Shift+click: open in a new pane`}
              >
                <input
                  type="checkbox"
                  checked={reviewed}
                  onClick={(e) => e.stopPropagation()}
                  onChange={(e) => setReviewed(key, [f.path], e.target.checked)}
                  aria-label={`Mark ${f.path} reviewed`}
                />
                <StatusBadge status={f.status} />
                <span className="file-name">
                  <span className="base">{basename(f.path)}</span>
                  <span className="dir muted">{dirname(f.path)}</span>
                </span>
                {openInPane && <span className="pill" title="Open in a pane">⧉</span>}
                {notes && <span className="pill">💬 {notes}</span>}
                <span className="stat small">
                  <span className="add">+{f.additions}</span> <span className="del">−{f.deletions}</span>
                </span>
                <button
                  className="icon-btn hover-only"
                  title="Open in a new pane"
                  onClick={(e) => {
                    e.stopPropagation();
                    open(f, true);
                  }}
                >
                  ⧉
                </button>
              </div>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
