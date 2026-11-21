import { useCallback, useEffect, useRef, useState } from 'react';
import { DiffStyleToggle, LayoutToggle, ThemeToggle, ViewMenu } from '../components/Controls';
import { FileBrowser, FinishDialog, ShortcutsDialog } from '../components/Dialogs';
import { FileCard, Pane } from '../components/FileViews';
import { CommitHeader, NotesPanel, neighbours } from '../components/Panels';
import { Sidebar } from '../components/Sidebar';
import { api } from '../lib/api';
import { overallProgress, shortSha } from '../lib/diff';
import { SessionProvider, useSession } from '../lib/session';
import { RANGE, REVIEW_STATUSES, SINCE, type Review } from '../lib/types';

export function ReviewPage({ id }: { id: string }) {
  const [review, setReview] = useState<Review | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setReview(null);
    api.getReview(id).then(setReview, (e: Error) => setError(e.message));
  }, [id]);

  if (error)
    return (
      <div className="center-page">
        <h2>Could not open review</h2>
        <p className="error">{error}</p>
        <a className="btn" href="#/">
          ← Back to reviews
        </a>
      </div>
    );
  if (!review) return <div className="center-page muted">Loading review…</div>;
  return (
    <SessionProvider key={review.id} initial={review}>
      <ReviewWorkspace />
    </SessionProvider>
  );
}

type Dialog = 'browse' | 'finish' | 'help' | null;

function ReviewWorkspace() {
  const session = useSession();
  const { review, ui, setUi, files, commitKeys, selectCommit, setReviewed, isReviewed, openPane, setDraft, error, setError } = session;
  const [dialog, setDialog] = useState<Dialog>(null);
  const mainRef = useRef<HTMLDivElement>(null);
  const fileList = files(ui.commitKey);

  useEffect(() => {
    document.title = `${review.name} · ReviewTool`;
  }, [review.name]);

  const scrollToFile = useCallback((path: string) => {
    const el = mainRef.current?.querySelector<HTMLElement>(`.file-card[data-path="${CSS.escape(path)}"]`);
    if (el && mainRef.current) mainRef.current.scrollTo({ top: el.offsetTop - 8 });
  }, []);

  const markAndNext = useCallback(async () => {
    const list = fileList ?? (await session.loadFiles(ui.commitKey));
    if (list.length > 0) await setReviewed(ui.commitKey, list.map((f) => f.path), true);
    const { next } = neighbours(commitKeys, ui.commitKey);
    if (next) selectCommit(next);
  }, [fileList, session, ui.commitKey, setReviewed, commitKeys, selectCommit]);

  const moveFile = useCallback(
    (delta: number) => {
      if (!fileList || fileList.length === 0) return;
      const idx = fileList.findIndex((f) => f.path === ui.currentFile);
      const next = fileList[Math.max(0, Math.min(fileList.length - 1, idx < 0 ? 0 : idx + delta))];
      if (ui.layout === 'panes') openPane({ path: next.path, oldPath: next.oldPath, mode: 'diff' });
      else {
        setUi({ currentFile: next.path });
        scrollToFile(next.path);
      }
    },
    [fileList, ui.currentFile, ui.layout, openPane, setUi, scrollToFile],
  );

  // ---- Keyboard shortcuts ----
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement;
      if (t.closest('input, textarea, select, [contenteditable="true"]') || e.ctrlKey || e.metaKey || e.altKey || dialog) return;
      const { prev, next } = neighbours(commitKeys, ui.commitKey);
      const handlers: Record<string, () => void> = {
        n: () => next && selectCommit(next),
        ']': () => next && selectCommit(next),
        p: () => prev && selectCommit(prev),
        '[': () => prev && selectCommit(prev),
        a: () => selectCommit(RANGE),
        j: () => moveFile(1),
        k: () => moveFile(-1),
        v: () => ui.currentFile && setReviewed(ui.commitKey, [ui.currentFile], !isReviewed(ui.commitKey, ui.currentFile)),
        M: () => void markAndNext(),
        u: () => setUi({ diffStyle: ui.diffStyle === 'split' ? 'unified' : 'split' }),
        l: () => toggleLayout(),
        w: () => setUi({ wrap: !ui.wrap }),
        i: () => setUi({ ignoreWhitespace: !ui.ignoreWhitespace }),
        d: () => setUi({ showLater: !ui.showLater }),
        s: () => review.previousHead && selectCommit(SINCE),
        o: () => setDialog('browse'),
        c: () => setDraft({ commitKey: ui.commitKey, path: null, side: 'New', line: null, endLine: null, viewId: 'commit' }),
        N: () => setUi({ notesOpen: !ui.notesOpen }),
        '?': () => setDialog('help'),
      };
      const h = handlers[e.key];
      if (h) {
        e.preventDefault();
        h();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

  const toggleLayout = () => {
    if (ui.layout === 'stack') {
      const current = fileList?.find((f) => f.path === ui.currentFile) ?? fileList?.[0];
      if (ui.panes.length === 0 && current) openPane({ path: current.path, oldPath: current.oldPath, mode: 'diff' });
      else setUi({ layout: 'panes' });
    } else setUi({ layout: 'stack' });
  };

  // Scroll back to the top when switching commits; restore the last file on first load.
  const restored = useRef(false);
  useEffect(() => {
    if (ui.layout !== 'stack' || !fileList) return;
    if (!restored.current && ui.currentFile) {
      restored.current = true;
      requestAnimationFrame(() => scrollToFile(ui.currentFile!));
      return;
    }
    restored.current = true;
    if (!ui.currentFile) mainRef.current?.scrollTo({ top: 0 });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ui.commitKey, fileList, ui.layout]);

  // Track which file is at the top of the stacked view, so "j/k" and "v" act on it.
  useEffect(() => {
    const el = mainRef.current;
    if (!el || ui.layout !== 'stack') return;
    let frame = 0;
    const onScroll = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => {
        const top = el.getBoundingClientRect().top + 80;
        const cards = el.querySelectorAll<HTMLElement>('.file-card');
        for (const c of cards) {
          if (c.getBoundingClientRect().bottom > top) {
            const path = c.dataset.path!;
            if (path !== ui.currentFile) setUi({ currentFile: path });
            break;
          }
        }
      });
    };
    el.addEventListener('scroll', onScroll, { passive: true });
    return () => {
      el.removeEventListener('scroll', onScroll);
      cancelAnimationFrame(frame);
    };
  }, [ui.layout, ui.currentFile, setUi]);

  // Notes panel jumps: scroll to the file card (DiffView scrolls to the line).
  useEffect(() => {
    if (!session.jump || ui.layout !== 'stack' || !session.jump.path) return;
    const path = session.jump.path;
    requestAnimationFrame(() => scrollToFile(path));
  }, [session.jump, ui.layout, scrollToFile]);

  const panes = ui.panes;
  const activePane = panes.some((p) => p.id === ui.activePane) ? ui.activePane : panes[0]?.id;

  return (
    <div className="workspace">
      <TopBar onFinish={() => setDialog('finish')} onHelp={() => setDialog('help')} onToggleLayout={toggleLayout} />
      {error && (
        <div className="banner error">
          {error}
          <button className="icon-btn" onClick={() => setError(null)} aria-label="Dismiss">
            ×
          </button>
        </div>
      )}
      <div className="workspace-body">
        <Sidebar onBrowse={() => setDialog('browse')} />
        <main className={`main ${ui.layout}`} ref={mainRef}>
          <CommitHeader onMarkAndNext={markAndNext} />
          {ui.layout === 'stack' ? (
            <div className="file-stack">
              {!fileList && <div className="muted pad">Loading files…</div>}
              {fileList?.length === 0 && <div className="muted pad">This commit doesn't change any files.</div>}
              {fileList?.map((f) => <FileCard key={`${ui.commitKey}:${f.path}`} commitKey={ui.commitKey} file={f} />)}
            </div>
          ) : (
            <div className="pane-grid" style={{ gridTemplateColumns: `repeat(${Math.max(1, panes.length)}, minmax(0, 1fr))` }}>
              {panes.length === 0 && (
                <div className="muted pad">Pick a file in the sidebar, or press <kbd>o</kbd> to open any file.</div>
              )}
              {panes.map((p) => (
                <Pane key={p.id} pane={p} active={p.id === activePane} canClose />
              ))}
            </div>
          )}
        </main>
        {ui.notesOpen && <NotesPanel onClose={() => setUi({ notesOpen: false })} onExport={() => setDialog('finish')} />}
      </div>
      {dialog === 'browse' && <FileBrowser onClose={() => setDialog(null)} />}
      {dialog === 'finish' && <FinishDialog onClose={() => setDialog(null)} />}
      {dialog === 'help' && <ShortcutsDialog onClose={() => setDialog(null)} />}
    </div>
  );
}

function TopBar({ onFinish, onHelp, onToggleLayout }: { onFinish: () => void; onHelp: () => void; onToggleLayout: () => void }) {
  const { review, ui, setUi, saveState, refresh, patchReview } = useSession();
  const [editingName, setEditingName] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const progress = overallProgress(review.commits, review.reviewedFiles);
  const openNotes = review.notes.filter((n) => !n.resolved).length;
  const status = REVIEW_STATUSES.find((s) => s.value === review.status);

  return (
    <header className="topbar">
      <a className="brand" href="#/" title="All reviews">
        ←
      </a>
      <div className="topbar-title">
        {editingName ? (
          <input
            className="input small"
            autoFocus
            defaultValue={review.name}
            onBlur={(e) => {
              setEditingName(false);
              if (e.target.value.trim() && e.target.value !== review.name) patchReview({ name: e.target.value.trim() });
            }}
            onKeyDown={(e) => {
              if (e.key === 'Enter') (e.target as HTMLInputElement).blur();
              if (e.key === 'Escape') setEditingName(false);
            }}
          />
        ) : (
          <h1 onDoubleClick={() => setEditingName(true)} title="Double-click to rename">
            {review.name}
          </h1>
        )}
        <div className="muted small ellipsis" title={review.repoPath}>
          <span className="mono">{review.baseRef}</span> ← <span className="mono">{review.headRef}</span> ({shortSha(review.headSha)}) ·{' '}
          {review.repoPath}
        </div>
      </div>
      <div className="topbar-progress" title={`${progress.reviewed} of ${progress.total} file changes reviewed`}>
        <div className="progress">
          <div className="progress-fill" style={{ width: `${Math.round(progress.ratio * 100)}%` }} />
        </div>
        <span className="small muted">
          {progress.commitsDone}/{progress.commitCount} commits · {progress.reviewed}/{progress.total} files
        </span>
      </div>
      <span className={`save-state ${saveState}`}>{saveState === 'saving' ? 'Saving…' : saveState === 'error' ? 'Save failed' : saveState === 'saved' ? 'Saved' : ''}</span>
      <span className="spacer" />
      <LayoutToggle value={ui.layout} onToggle={onToggleLayout} />
      <DiffStyleToggle value={ui.diffStyle} onChange={(v) => setUi({ diffStyle: v })} />
      <ViewMenu />
      <button
        className="btn small"
        disabled={refreshing}
        title="Re-read the branches (picks up new pushes)"
        onClick={async () => {
          setRefreshing(true);
          await refresh();
          setRefreshing(false);
        }}
      >
        {refreshing ? 'Refreshing…' : '⟳ Refresh'}
      </button>
      <button className={`btn small ${ui.notesOpen ? 'active' : ''}`} onClick={() => setUi({ notesOpen: !ui.notesOpen })} title="Notes panel (Shift+N)">
        💬 Notes{openNotes > 0 ? ` (${openNotes})` : ''}
      </button>
      <button className="btn small primary" onClick={onFinish} title="Summary, status and Markdown export">
        {status && review.status !== 'InProgress' ? status.label : 'Finish'}
      </button>
      <ThemeToggle />
      <button className="icon-btn" onClick={onHelp} title="Keyboard shortcuts (?)">
        ?
      </button>
    </header>
  );
}
