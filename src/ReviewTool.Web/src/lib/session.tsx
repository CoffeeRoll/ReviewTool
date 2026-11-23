import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { api } from './api';
import {
  RANGE,
  SINCE,
  type BlobContent,
  type ChangedFile,
  type DiffSide,
  type FileDiff,
  type LaterChange,
  type MovedBlock,
  type NoteFollowUp,
  type NoteInput,
  type PaneSpec,
  type Review,
  type ReviewNote,
  type ReviewStatus,
  type UiState,
} from './types';

export const DEFAULT_UI: Required<Omit<UiState, 'activePane' | 'currentFile'>> & Pick<UiState, 'activePane' | 'currentFile'> = {
  commitKey: '',
  layout: 'stack',
  diffStyle: 'unified',
  context: 3,
  wrap: false,
  panes: [],
  activePane: undefined,
  notesOpen: false,
  currentFile: null,
  sidebarWidth: 330,
  ignoreWhitespace: false,
  fileTree: true,
  showLater: true,
};

export type Ui = typeof DEFAULT_UI;

/** An in-progress note that hasn't been saved yet. */
export interface NoteDraft {
  commitKey: string;
  path: string | null;
  side: DiffSide;
  line: number | null;
  endLine: number | null;
  /** Identifies which view opened the draft so duplicates of the same file don't all show the editor. */
  viewId: string;
}

export interface JumpTarget {
  commitKey: string;
  path: string | null;
  side?: DiffSide;
  line?: number | null;
  nonce: number;
}

export type SaveState = 'idle' | 'saving' | 'saved' | 'error';

interface SessionValue {
  review: Review;
  ui: Ui;
  setUi: (patch: Partial<Ui> | ((ui: Ui) => Partial<Ui>)) => void;
  saveState: SaveState;
  error: string | null;
  setError: (e: string | null) => void;

  /** Ordered keys used for next/previous navigation (range is not included). */
  commitKeys: string[];
  selectCommit: (key: string) => void;

  files: (key: string) => ChangedFile[] | undefined;
  loadFiles: (key: string) => Promise<ChangedFile[]>;
  getDiff: (key: string, path: string, oldPath: string | null | undefined, context: number) => Promise<FileDiff>;

  isReviewed: (key: string, path: string) => boolean;
  setReviewed: (key: string, paths: string[], reviewed: boolean) => Promise<void>;
  /** Reviewed changed-line ranges of a partially reviewed file. */
  reviewedLines: (key: string, path: string) => string[] | undefined;
  setLinesReviewed: (key: string, path: string, ranges: string[], reviewed: boolean) => Promise<void>;

  /** Moved-code blocks of a commit (loaded on demand; undefined while loading). */
  moved: (key: string) => MovedBlock[] | undefined;
  loadMoved: (key: string) => void;
  /** Lines of a file at a commit that later commits in the review change. */
  getLaterChanges: (key: string, path: string) => Promise<LaterChange[]>;
  /** Open notes whose code a later commit changes. */
  followUps: NoteFollowUp[];
  /** File contents at a commit (side "Old" = before the commit), cached for the session. */
  getBlob: (key: string, path: string, side?: DiffSide) => Promise<BlobContent>;

  draft: NoteDraft | null;
  setDraft: (d: NoteDraft | null) => void;
  addNote: (input: NoteInput) => Promise<ReviewNote | undefined>;
  updateNote: (noteId: string, input: NoteInput) => Promise<void>;
  deleteNote: (noteId: string) => Promise<void>;

  patchReview: (p: { name?: string; summary?: string; status?: ReviewStatus }) => Promise<void>;
  refresh: () => Promise<void>;

  jump: JumpTarget | null;
  jumpTo: (t: Omit<JumpTarget, 'nonce'>) => void;

  openPane: (spec: Omit<PaneSpec, 'id'>, opts?: { newPane?: boolean }) => void;
}

const SessionContext = createContext<SessionValue | null>(null);

export function useSession(): SessionValue {
  const v = useContext(SessionContext);
  if (!v) throw new Error('useSession must be used inside <SessionProvider>');
  return v;
}

const MAX_PANES = 4;
let paneCounter = 0;
export const newPaneId = () => `p${Date.now().toString(36)}${(paneCounter++).toString(36)}`;

export function SessionProvider({ initial, children }: { initial: Review; children: ReactNode }) {
  const [review, setReview] = useState<Review>(initial);
  const [ui, setUiState] = useState<Ui>(() => {
    const saved = { ...DEFAULT_UI, ...(initial.uiState ?? {}) } as Ui;
    const valid =
      saved.commitKey === RANGE ||
      (saved.commitKey === SINCE && !!initial.previousHead) ||
      initial.commits.some((c) => c.sha === saved.commitKey);
    if (!valid) saved.commitKey = initial.commits.find((c) => !c.outdated)?.sha ?? RANGE;
    return saved;
  });
  const [saveState, setSaveState] = useState<SaveState>('idle');
  const [error, setError] = useState<string | null>(null);
  const [filesByKey, setFilesByKey] = useState<Record<string, ChangedFile[]>>({});
  const [draft, setDraft] = useState<NoteDraft | null>(null);
  const [jump, setJump] = useState<JumpTarget | null>(null);
  const [movedByKey, setMovedByKey] = useState<Record<string, MovedBlock[]>>({});
  const [followUps, setFollowUps] = useState<NoteFollowUp[]>([]);
  const pendingMoved = useRef(new Set<string>());
  const laterCache = useRef(new Map<string, Promise<LaterChange[]>>());
  const blobCache = useRef(new Map<string, Promise<BlobContent>>());
  const pendingFiles = useRef(new Map<string, Promise<ChangedFile[]>>());
  const diffCache = useRef(new Map<string, Promise<FileDiff>>());
  const reviewId = review.id;

  // ---- Persist UI state (debounced) so the review can be resumed exactly where it was left. ----
  const uiRef = useRef(ui);
  uiRef.current = ui;
  const saveTimer = useRef<number | undefined>(undefined);
  const firstRender = useRef(true);
  useEffect(() => {
    if (firstRender.current) {
      firstRender.current = false;
      return;
    }
    window.clearTimeout(saveTimer.current);
    saveTimer.current = window.setTimeout(async () => {
      try {
        setSaveState('saving');
        await api.patchReview(reviewId, { uiState: uiRef.current });
        setSaveState('saved');
      } catch (e) {
        setSaveState('error');
        setError(`Could not save view state: ${(e as Error).message}`);
      }
    }, 700);
  }, [ui, reviewId]);

  // Flush the latest UI state when leaving the page.
  useEffect(() => {
    const flush = () => api.saveUiKeepalive(reviewId, uiRef.current);
    window.addEventListener('beforeunload', flush);
    return () => {
      window.removeEventListener('beforeunload', flush);
      window.clearTimeout(saveTimer.current);
      flush();
    };
  }, [reviewId]);

  const setUi = useCallback((patch: Partial<Ui> | ((ui: Ui) => Partial<Ui>)) => {
    setUiState((prev) => ({ ...prev, ...(typeof patch === 'function' ? patch(prev) : patch) }));
  }, []);

  const track = useCallback(async <T,>(fn: () => Promise<T>): Promise<T | undefined> => {
    setSaveState('saving');
    try {
      const result = await fn();
      setSaveState('saved');
      return result;
    } catch (e) {
      setSaveState('error');
      setError((e as Error).message);
      return undefined;
    }
  }, []);

  const commitKeys = useMemo(() => review.commits.filter((c) => !c.outdated).map((c) => c.sha), [review.commits]);

  const selectCommit = useCallback(
    (key: string) => {
      setUi({ commitKey: key, currentFile: null });
      setDraft(null);
    },
    [setUi],
  );

  const ws = !!ui.ignoreWhitespace;
  const cacheKey = useCallback(
    (key: string) =>
      (key === RANGE
        ? `${RANGE}@${review.mergeBaseSha}..${review.headSha}`
        : key === SINCE
          ? `${SINCE}@${review.previousHead?.headSha}..${review.headSha}`
          : key) + (ws ? '|w' : ''),
    [review.mergeBaseSha, review.headSha, review.previousHead?.headSha, ws],
  );

  const loadFiles = useCallback(
    (key: string) => {
      const ck = cacheKey(key);
      const existing = pendingFiles.current.get(ck);
      if (existing) return existing;
      const p = api.changedFiles(reviewId, key, ws).then(
        (files) => {
          setFilesByKey((prev) => ({ ...prev, [ck]: files }));
          return files;
        },
        (e) => {
          pendingFiles.current.delete(ck);
          throw e;
        },
      );
      pendingFiles.current.set(ck, p);
      return p;
    },
    [reviewId, cacheKey, ws],
  );

  const files = useCallback((key: string) => filesByKey[cacheKey(key)], [filesByKey, cacheKey]);

  const getDiff = useCallback(
    (key: string, path: string, oldPath: string | null | undefined, context: number) => {
      const k = `${cacheKey(key)}|${path}|${oldPath ?? ''}|${context}`;
      const hit = diffCache.current.get(k);
      if (hit) return hit;
      const p = api.fileDiff(reviewId, key, path, oldPath, context, ws);
      p.catch(() => diffCache.current.delete(k));
      diffCache.current.set(k, p);
      return p;
    },
    [reviewId, cacheKey, ws],
  );

  const moved = useCallback((key: string) => movedByKey[cacheKey(key)], [movedByKey, cacheKey]);
  const loadMoved = useCallback(
    (key: string) => {
      const ck = cacheKey(key);
      if (pendingMoved.current.has(ck)) return;
      pendingMoved.current.add(ck);
      api.moved(reviewId, key, ws).then(
        (blocks) => setMovedByKey((prev) => ({ ...prev, [ck]: blocks })),
        () => setMovedByKey((prev) => ({ ...prev, [ck]: [] })),
      );
    },
    [reviewId, cacheKey, ws],
  );

  const getLaterChanges = useCallback(
    (key: string, path: string) => {
      const k = `${key}|${review.headSha}|${path}`;
      let p = laterCache.current.get(k);
      if (!p) {
        p = api.laterChanges(reviewId, key, path).catch(() => [] as LaterChange[]);
        laterCache.current.set(k, p);
      }
      return p;
    },
    [reviewId, review.headSha],
  );

  const getBlob = useCallback(
    (key: string, path: string, side: DiffSide = 'New') => {
      const k = `${cacheKey(key)}|${side}|${path}`;
      let p = blobCache.current.get(k);
      if (!p) {
        p = api.blob(reviewId, key, path, side);
        p.catch(() => blobCache.current.delete(k));
        if (blobCache.current.size > 200) blobCache.current.clear();
        blobCache.current.set(k, p);
      }
      return p;
    },
    [reviewId, cacheKey],
  );

  // Follow-ups depend on notes and on the commit list; recompute when either changes.
  const notesSignature = review.notes.map((n) => `${n.id}:${n.line}:${n.endLine}:${n.resolved}`).join(',');
  useEffect(() => {
    let cancelled = false;
    api.followUps(reviewId).then(
      (f) => !cancelled && setFollowUps(f),
      () => undefined,
    );
    return () => {
      cancelled = true;
    };
  }, [reviewId, notesSignature, review.headSha]);

  const reviewedSets = useMemo(() => {
    const m = new Map<string, Set<string>>();
    for (const [k, v] of Object.entries(review.reviewedFiles ?? {})) m.set(k, new Set(v));
    return m;
  }, [review.reviewedFiles]);

  const isReviewed = useCallback((key: string, path: string) => reviewedSets.get(key)?.has(path) ?? false, [reviewedSets]);

  const reviewedLines = useCallback((key: string, path: string) => review.reviewedLines?.[key]?.[path], [review.reviewedLines]);

  const setLinesReviewed = useCallback(
    async (key: string, path: string, ranges: string[], reviewed: boolean) => {
      const updated = await track(() => api.setLinesReviewed(reviewId, key, path, ranges, reviewed));
      if (updated) setReview((r) => ({ ...r, reviewedLines: updated.reviewedLines, updatedAt: updated.updatedAt }));
    },
    [reviewId, track],
  );

  const setReviewed = useCallback(
    async (key: string, paths: string[], reviewed: boolean) => {
      // Optimistic update so checkboxes feel instant.
      setReview((r) => {
        const set = new Set(r.reviewedFiles[key] ?? []);
        for (const p of paths) {
          if (reviewed) set.add(p);
          else set.delete(p);
        }
        return { ...r, reviewedFiles: { ...r.reviewedFiles, [key]: [...set].sort() } };
      });
      const updated = await track(() => api.setReviewed(reviewId, key, paths, reviewed));
      if (updated)
        setReview((r) => ({ ...r, reviewedFiles: updated.reviewedFiles, reviewedLines: updated.reviewedLines, updatedAt: updated.updatedAt }));
    },
    [reviewId, track],
  );

  const addNote = useCallback(
    async (input: NoteInput) => {
      const note = await track(() => api.addNote(reviewId, input));
      if (note) setReview((r) => ({ ...r, notes: [...r.notes, note] }));
      return note;
    },
    [reviewId, track],
  );

  const updateNote = useCallback(
    async (noteId: string, input: NoteInput) => {
      const note = await track(() => api.updateNote(reviewId, noteId, input));
      if (note) setReview((r) => ({ ...r, notes: r.notes.map((n) => (n.id === noteId ? note : n)) }));
    },
    [reviewId, track],
  );

  const deleteNote = useCallback(
    async (noteId: string) => {
      setReview((r) => ({ ...r, notes: r.notes.filter((n) => n.id !== noteId) }));
      await track(() => api.deleteNote(reviewId, noteId));
    },
    [reviewId, track],
  );

  const patchReview = useCallback(
    async (p: { name?: string; summary?: string; status?: ReviewStatus }) => {
      setReview((r) => ({ ...r, ...p }) as Review);
      await track(() => api.patchReview(reviewId, p));
    },
    [reviewId, track],
  );

  const refresh = useCallback(async () => {
    const before = review.previousHead?.recordedAt;
    const updated = await track(() => api.refreshReview(reviewId));
    if (!updated) return;
    pendingFiles.current.clear();
    pendingMoved.current.clear();
    setFilesByKey({});
    setReview(updated);
    const headMoved = !!updated.previousHead && updated.previousHead.recordedAt !== before;
    setUiState((prev) => {
      // When new commits arrived, jump straight to what changed since the last review.
      if (headMoved) return { ...prev, commitKey: SINCE, currentFile: null };
      const ok = prev.commitKey === RANGE || (prev.commitKey === SINCE && !!updated.previousHead) || updated.commits.some((c) => c.sha === prev.commitKey);
      return ok ? prev : { ...prev, commitKey: updated.commits.find((c) => !c.outdated)?.sha ?? RANGE, currentFile: null };
    });
  }, [reviewId, track, review.previousHead?.recordedAt]);

  const jumpTo = useCallback(
    (t: Omit<JumpTarget, 'nonce'>) => {
      setUiState((prev) => ({
        ...prev,
        commitKey: t.commitKey,
        currentFile: t.path ?? prev.currentFile,
        // In pane layout, make sure the target file is visible in the active pane.
        panes:
          prev.layout === 'panes' && t.path && !prev.panes.some((p) => p.path === t.path && !p.pinnedCommit)
            ? replaceActive(prev, { path: t.path, mode: 'diff' })
            : prev.panes,
      }));
      setJump({ ...t, nonce: Date.now() });
    },
    [],
  );

  const openPane = useCallback((spec: Omit<PaneSpec, 'id'>, opts?: { newPane?: boolean }) => {
    setUiState((prev) => {
      if (opts?.newPane || prev.panes.length === 0) {
        const pane: PaneSpec = { ...spec, id: newPaneId() };
        const panes = [...prev.panes, pane].slice(-MAX_PANES);
        return { ...prev, layout: 'panes', panes, activePane: pane.id, currentFile: spec.path };
      }
      return { ...prev, layout: 'panes', panes: replaceActive(prev, spec), currentFile: spec.path };
    });
  }, []);

  const value: SessionValue = {
    review,
    ui,
    setUi,
    saveState,
    error,
    setError,
    commitKeys,
    selectCommit,
    files,
    loadFiles,
    getDiff,
    isReviewed,
    setReviewed,
    reviewedLines,
    setLinesReviewed,
    moved,
    loadMoved,
    getLaterChanges,
    followUps,
    getBlob,
    draft,
    setDraft,
    addNote,
    updateNote,
    deleteNote,
    patchReview,
    refresh,
    jump,
    jumpTo,
    openPane,
  };

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

function replaceActive(ui: Ui, spec: Omit<PaneSpec, 'id'>): PaneSpec[] {
  const activeId = ui.panes.some((p) => p.id === ui.activePane) ? ui.activePane : ui.panes[0]?.id;
  if (!activeId) return [{ ...spec, id: newPaneId() }];
  return ui.panes.map((p) => (p.id === activeId ? { ...p, ...spec, pinnedCommit: spec.pinnedCommit ?? p.pinnedCommit } : p));
}

/** Notes for one file at one commit (or range). */
export function notesFor(review: Review, commitKey: string, path: string | null) {
  return review.notes.filter((n) => n.commitSha === commitKey && (n.filePath ?? null) === path);
}
