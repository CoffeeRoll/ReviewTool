import { useEffect, useMemo, useRef, useState } from 'react';
import { api } from '../lib/api';
import { basename, dirname, shortSha } from '../lib/diff';
import { notesFor, useSession } from '../lib/session';
import { RANGE, SINCE, isSpecialKey, type BlobContent, type ChangedFile, type ChangeKind, type FileDiff, type FileHistoryEntry, type PaneSpec } from '../lib/types';
import { DiffView, fileAsDiff } from './DiffView';

export function StatusBadge({ status }: { status: ChangeKind }) {
  const letter = { Modified: 'M', Added: 'A', Deleted: 'D', Renamed: 'R', Copied: 'C', TypeChanged: 'T', Unknown: '?' }[status];
  return (
    <span className={`status status-${status.toLowerCase()}`} title={status}>
      {letter}
    </span>
  );
}

export function FilePath({ file }: { file: Pick<ChangedFile, 'path' | 'oldPath'> }) {
  const dir = dirname(file.path);
  return (
    <span className="file-path" title={file.oldPath ? `${file.oldPath} → ${file.path}` : file.path}>
      {file.oldPath && file.oldPath !== file.path && <span className="muted">{file.oldPath} → </span>}
      {dir && <span className="muted">{dir}/</span>}
      <strong>{basename(file.path)}</strong>
    </span>
  );
}

function useFileDiff(commitKey: string, path: string, oldPath: string | null | undefined, full: boolean, enabled = true) {
  const { getDiff, ui } = useSession();
  const [state, setState] = useState<{ diff?: FileDiff; error?: string; key?: string }>({});
  const context = full ? -1 : ui.context;
  const key = `${commitKey}|${path}|${oldPath}|${context}`;
  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    getDiff(commitKey, path, oldPath, context).then(
      (diff) => !cancelled && setState({ diff, key }),
      (e: Error) => !cancelled && setState({ error: e.message, key }),
    );
    return () => {
      cancelled = true;
    };
  }, [key, enabled, getDiff, commitKey, path, oldPath, context]);
  // While a new request is in flight keep showing the previous diff to avoid flicker.
  return { ...state, loading: state.key !== key };
}

function useBlob(commitKey: string, path: string, enabled = true) {
  const { getBlob } = useSession();
  const [state, setState] = useState<{ blob?: BlobContent; error?: string; key?: string }>({});
  const key = `${commitKey}|${path}`;
  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    getBlob(commitKey, path).then(
      (blob) => !cancelled && setState({ blob, key }),
      (e: Error) => !cancelled && setState({ error: e.message, key }),
    );
    return () => {
      cancelled = true;
    };
  }, [key, enabled, getBlob, commitKey, path]);
  return { ...state, loading: state.key !== key };
}

/** One changed file in the stacked (GitHub-style) layout. Loads its diff lazily when scrolled near. */
export function FileCard({ commitKey, file }: { commitKey: string; file: ChangedFile }) {
  const { review, isReviewed, setReviewed, setDraft, openPane, setUi, jump, getLaterChanges, reviewedLines } = useSession();
  const reviewed = isReviewed(commitKey, file.path);
  // Reviewed and generated files start collapsed.
  const [collapsed, setCollapsed] = useState(reviewed || file.isGenerated);
  const [laterCount, setLaterCount] = useState(0);
  const partial = !reviewed && (reviewedLines(commitKey, file.path)?.length ?? 0) > 0;

  useEffect(() => {
    if (isSpecialKey(commitKey) || file.status === 'Deleted' || file.isBinary) return;
    let cancelled = false;
    getLaterChanges(commitKey, file.path).then((l) => !cancelled && setLaterCount(l.length));
    return () => {
      cancelled = true;
    };
  }, [commitKey, file.path, file.status, file.isBinary, getLaterChanges]);
  const [visible, setVisible] = useState(false);
  const [full, setFull] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const noteCount = useMemo(() => notesFor(review, commitKey, file.path).length, [review, commitKey, file.path]);

  useEffect(() => {
    const el = ref.current;
    if (!el || visible) return;
    const io = new IntersectionObserver((entries) => entries.some((e) => e.isIntersecting) && setVisible(true), { rootMargin: '1200px 0px' });
    io.observe(el);
    return () => io.disconnect();
  }, [visible]);

  const { diff, error, loading } = useFileDiff(commitKey, file.path, file.oldPath, full, visible && !collapsed);

  // Expand when a note in this file is jumped to.
  useEffect(() => {
    if (jump && jump.commitKey === commitKey && jump.path === file.path) {
      setCollapsed(false);
      setVisible(true);
    }
  }, [jump, commitKey, file.path]);

  // Reviewed files collapse (like GitHub's "Viewed"), including when marked from elsewhere.
  const prevReviewed = useRef(reviewed);
  useEffect(() => {
    if (prevReviewed.current === reviewed) return;
    prevReviewed.current = reviewed;
    setCollapsed(reviewed);
  }, [reviewed]);
  const toggleReviewed = (value: boolean) => setReviewed(commitKey, [file.path], value);

  return (
    <div className={`file-card ${reviewed ? 'reviewed' : ''}`} ref={ref} data-path={file.path}>
      <div className="file-card-head" onClick={() => setUi({ currentFile: file.path })}>
        <button className="icon-btn chevron" aria-label={collapsed ? 'Expand' : 'Collapse'} onClick={() => setCollapsed((c) => !c)}>
          {collapsed ? '▸' : '▾'}
        </button>
        <StatusBadge status={file.status} />
        <FilePath file={file} />
        <span className="stat">
          <span className="add">+{file.additions}</span> <span className="del">−{file.deletions}</span>
        </span>
        {noteCount > 0 && <span className="pill">{noteCount} note{noteCount === 1 ? '' : 's'}</span>}
        {file.isGenerated && (
          <span className="pill" title="Generated file (lockfile, designer file or marked linguist-generated) — collapsed by default">
            generated
          </span>
        )}
        {partial && <span className="pill">partly reviewed</span>}
        {laterCount > 0 && (
          <span className="pill later-pill" title="Lines of this file (as of this commit) that a later commit in this review changes again — added lines are marked ↻ in the diff">
            ↻ {laterCount} line{laterCount === 1 ? '' : 's'} change later
          </span>
        )}
        <span className="spacer" />
        <button
          className="btn small"
          title="Add a note on the whole file"
          onClick={() => {
            setCollapsed(false);
            setDraft({ commitKey, path: file.path, side: 'New', line: null, endLine: null, viewId: 'stack' });
          }}
        >
          💬 Note
        </button>
        {!file.isBinary && (
          <button className={`btn small ${full ? 'active' : ''}`} title="Toggle full-file context" onClick={() => setFull((f) => !f)}>
            {full ? 'Collapse context' : 'Full file'}
          </button>
        )}
        <button
          className="btn small"
          title="Open in a side-by-side pane"
          onClick={() => openPane({ path: file.path, oldPath: file.oldPath, mode: 'diff' }, { newPane: true })}
        >
          ⧉ Pane
        </button>
        <label className="viewed" onClick={(e) => e.stopPropagation()}>
          <input type="checkbox" checked={reviewed} onChange={(e) => toggleReviewed(e.target.checked)} /> Reviewed
        </label>
      </div>
      {!collapsed && (
        <div className="file-card-body">
          {error ? (
            <div className="diff-message error">{error}</div>
          ) : !diff ? (
            <div className="diff-message muted">{visible ? 'Loading…' : ''}</div>
          ) : (
            <div className={loading ? 'stale' : ''}>
              <DiffView viewId="stack" commitKey={commitKey} path={file.path} diff={diff} isFull={full} onExpand={() => setFull(true)} stale={loading} />
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/** A full file (not a diff) at a commit, with notes support. */
export function FileContentView({ viewId, commitKey, path }: { viewId: string; commitKey: string; path: string }) {
  const { blob, error, loading } = useBlob(commitKey, path);
  const diff = useMemo(() => (blob?.text != null ? fileAsDiff(path, blob.text) : null), [blob, path]);
  if (error && /does not exist/.test(error)) return <div className="diff-message">This file does not exist at this commit.</div>;
  if (error) return <div className="diff-message error">{error}</div>;
  if (!blob) return <div className="diff-message muted">{loading ? 'Loading…' : ''}</div>;
  if (blob.isBinary) return <div className="diff-message">Binary file — not shown.</div>;
  if (blob.truncated) return <div className="diff-message">File is too large to display ({Math.round(blob.size / 1024)} KB).</div>;
  return <DiffView viewId={viewId} commitKey={commitKey} path={path} diff={diff!} plain stale={loading} />;
}

/** One column in the multi-pane layout. */
export function Pane({ pane, active, canClose }: { pane: PaneSpec; active: boolean; canClose: boolean }) {
  const { review, ui, setUi, files, loadFiles, isReviewed, setReviewed, setDraft } = useSession();
  const [full, setFull] = useState(false);
  const [history, setHistory] = useState<FileHistoryEntry[] | null>(null);
  const isHistory = pane.mode === 'history';

  useEffect(() => {
    if (!isHistory) return;
    let cancelled = false;
    setHistory(null);
    api.fileHistory(review.id, pane.path).then(
      (h) => !cancelled && setHistory(h),
      () => !cancelled && setHistory([]),
    );
    return () => {
      cancelled = true;
    };
  }, [isHistory, review.id, pane.path, review.headSha]);

  // In history mode, pick the selected step, else the latest change at or before the current commit.
  const order = useMemo(() => new Map(review.commits.filter((c) => !c.outdated).map((c, i) => [c.sha, i])), [review.commits]);
  const step = useMemo(() => {
    if (!isHistory || !history || history.length === 0) return null;
    const chosen = history.find((h) => h.sha === pane.historyCommit);
    if (chosen) return chosen;
    const current = order.get(ui.commitKey) ?? Number.MAX_SAFE_INTEGER;
    return [...history].reverse().find((h) => (order.get(h.sha) ?? 0) <= current) ?? history[0];
  }, [isHistory, history, pane.historyCommit, order, ui.commitKey]);
  const stepIndex = step && history ? history.indexOf(step) : -1;

  const commitKey = isHistory ? (step?.sha ?? ui.commitKey) : (pane.pinnedCommit ?? ui.commitKey);
  const viewPath = isHistory && step ? step.path : pane.path;
  const changed = files(commitKey);

  useEffect(() => {
    loadFiles(commitKey).catch(() => undefined);
  }, [commitKey, loadFiles]);

  const file = changed?.find((f) => f.path === viewPath);
  const showsDiff = pane.mode === 'diff' || (isHistory && !!step);
  const effectiveMode = pane.mode === 'diff' && changed && !file ? 'missing' : pane.mode;
  const { diff, error, loading } = useFileDiff(commitKey, viewPath, file?.oldPath ?? (isHistory ? step?.oldPath : pane.oldPath), full, showsDiff && !!file);

  const update = (patch: Partial<PaneSpec>) =>
    setUi((u) => ({ panes: u.panes.map((p) => (p.id === pane.id ? { ...p, ...patch } : p)) }));
  const close = () =>
    setUi((u) => {
      const panes = u.panes.filter((p) => p.id !== pane.id);
      return { panes, activePane: u.activePane === pane.id ? panes[0]?.id : u.activePane, layout: panes.length ? u.layout : 'stack' };
    });

  const commitLabel = commitKey === RANGE ? 'All changes' : commitKey === SINCE ? 'Since last review' : shortSha(commitKey);
  const reviewed = file ? isReviewed(commitKey, file.path) : false;
  const options = useMemo(() => {
    const list = (files(ui.commitKey) ?? changed ?? []).map((f) => f.path);
    if (!list.includes(pane.path)) list.unshift(pane.path);
    return list;
  }, [files, ui.commitKey, changed, pane.path]);

  const stepCommit = step ? review.commits.find((c) => c.sha === step.sha) : null;

  return (
    <section className={`pane ${active ? 'active' : ''}`} onMouseDown={() => !active && setUi({ activePane: pane.id, currentFile: pane.path })}>
      <header className="pane-head">
        <select
          className="pane-file"
          value={pane.path}
          onChange={(e) => {
            const f = changed?.find((c) => c.path === e.target.value);
            update({ path: e.target.value, oldPath: f?.oldPath ?? null, historyCommit: null });
            setUi({ currentFile: e.target.value });
          }}
        >
          {options.map((p) => (
            <option key={p} value={p}>
              {p}
            </option>
          ))}
        </select>
        <div className="seg">
          <button className={pane.mode === 'diff' ? 'on' : ''} onClick={() => update({ mode: 'diff' })} title="Changes in the current commit">
            Diff
          </button>
          <button className={pane.mode === 'file' ? 'on' : ''} onClick={() => update({ mode: 'file' })} title="The whole file at this commit">
            File
          </button>
          <button
            className={isHistory ? 'on' : ''}
            onClick={() => update({ mode: 'history', historyCommit: null })}
            title="Step through every commit in this review that changes the file"
          >
            History
          </button>
        </div>
        {!isHistory && (
          <button
            className={`btn small ${pane.pinnedCommit ? 'active' : ''}`}
            title={pane.pinnedCommit ? 'Pinned to this commit — click to follow the current commit' : 'Pin this pane to the current commit'}
            onClick={() => update({ pinnedCommit: pane.pinnedCommit ? null : ui.commitKey })}
          >
            📌 {pane.pinnedCommit ? commitLabel : 'Pin'}
          </button>
        )}
        {canClose && (
          <button className="icon-btn" aria-label="Close pane" onClick={close}>
            ×
          </button>
        )}
      </header>
      {isHistory && (
        <div className="history-bar">
          {!history ? (
            <span className="muted small">Loading history…</span>
          ) : history.length === 0 ? (
            <span className="muted small">No commit in this review changes this file.</span>
          ) : (
            <>
              <button className="icon-btn" disabled={stepIndex <= 0} onClick={() => update({ historyCommit: history[stepIndex - 1].sha })} aria-label="Previous change">
                ◀
              </button>
              <div className="history-dots">
                {history.map((h, i) => {
                  const c = review.commits.find((x) => x.sha === h.sha);
                  return (
                    <button
                      key={h.sha}
                      className={`dot ${i === stepIndex ? 'on' : ''} ${h.sha === ui.commitKey ? 'current' : ''}`}
                      title={`Commit ${(order.get(h.sha) ?? 0) + 1}: ${c?.subject ?? ''}${h.oldPath ? ` (renamed from ${h.oldPath})` : ''}`}
                      onClick={() => update({ historyCommit: h.sha })}
                    >
                      {(order.get(h.sha) ?? 0) + 1}
                    </button>
                  );
                })}
              </div>
              <button
                className="icon-btn"
                disabled={stepIndex >= history.length - 1}
                onClick={() => update({ historyCommit: history[stepIndex + 1].sha })}
                aria-label="Next change"
              >
                ▶
              </button>
              <span className="small ellipsis history-subject" title={stepCommit?.subject}>
                {stepIndex + 1}/{history.length} · {stepCommit?.subject}
              </span>
            </>
          )}
        </div>
      )}
      {file && showsDiff && (
        <div className="pane-subhead">
          <StatusBadge status={file.status} />
          <span className="stat">
            <span className="add">+{file.additions}</span> <span className="del">−{file.deletions}</span>
          </span>
          {isHistory && <span className="pill mono">{shortSha(commitKey)}</span>}
          <span className="spacer" />
          <button className="btn-link" onClick={() => setDraft({ commitKey, path: file.path, side: 'New', line: null, endLine: null, viewId: pane.id })}>
            💬 File note
          </button>
          {!file.isBinary && (
            <button className="btn-link" onClick={() => setFull((f) => !f)}>
              {full ? 'Collapse context' : 'Full file'}
            </button>
          )}
          <label className="viewed">
            <input type="checkbox" checked={reviewed} onChange={(e) => setReviewed(commitKey, [file.path], e.target.checked)} /> Reviewed
          </label>
        </div>
      )}
      <div className="pane-body">
        {effectiveMode === 'missing' ? (
          <div className="diff-message">
            <p>
              <code>{pane.path}</code> is not changed in {isSpecialKey(commitKey) ? 'this view' : `commit ${shortSha(commitKey)}`}.
            </p>
            <div className="row">
              <button className="btn" onClick={() => update({ mode: 'file' })}>
                Show the file at this commit
              </button>
              <button className="btn" onClick={() => update({ mode: 'history', historyCommit: null })}>
                Show its history
              </button>
            </div>
          </div>
        ) : pane.mode === 'file' ? (
          <FileContentView viewId={pane.id} commitKey={commitKey} path={pane.path} />
        ) : isHistory && !step ? null : error ? (
          <div className="diff-message error">{error}</div>
        ) : !diff ? (
          <div className="diff-message muted">Loading…</div>
        ) : (
          <div className={loading ? 'stale' : ''}>
            <DiffView viewId={pane.id} commitKey={commitKey} path={viewPath} diff={diff} isFull={full} onExpand={() => setFull(true)} stale={loading} />
          </div>
        )}
      </div>
    </section>
  );
}
