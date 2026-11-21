import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { api } from './api';
import {
  RANGE,
  type ChangedFile,
  type DiffSide,
  type FileDiff,
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
    const valid = saved.commitKey === RANGE || initial.commits.some((c) => c.sha === saved.commitKey);
    if (!valid) saved.commitKey = initial.commits.find((c) => !c.outdated)?.sha ?? RANGE;
    return saved;
  });
  const [saveState, setSaveState] = useState<SaveState>('idle');
  const [error, setError] = useState<string | null>(null);
  const [filesByKey, setFilesByKey] = useState<Record<string, ChangedFile[]>>({});
  const [draft, setDraft] = useState<NoteDraft | null>(null);
  const [jump, setJump] = useState<JumpTarget | null>(null);
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

  const cacheKey = useCallback(
    (key: string) => (key === RANGE ? `${RANGE}@${review.mergeBaseSha}..${review.headSha}` : key),
    [review.mergeBaseSha, review.headSha],
  );

  const loadFiles = useCallback(
    (key: string) => {
      const ck = cacheKey(key);
      const existing = pendingFiles.current.get(ck);
      if (existing) return existing;
      const p = api.changedFiles(reviewId, key).then(
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
    [reviewId, cacheKey],
  );

  const files = useCallback((key: string) => filesByKey[cacheKey(key)], [filesByKey, cacheKey]);

  const getDiff = useCallback(
    (key: string, path: string, oldPath: string | null | undefined, context: number) => {
      const k = `${cacheKey(key)}|${path}|${oldPath ?? ''}|${context}`;
      const hit = diffCache.current.get(k);
      if (hit) return hit;
      const p = api.fileDiff(reviewId, key, path, oldPath, context);
      p.catch(() => diffCache.current.delete(k));
      diffCache.current.set(k, p);
      return p;
    },
    [reviewId, cacheKey],
  );

  const reviewedSets = useMemo(() => {
    const m = new Map<string, Set<string>>();
    for (const [k, v] of Object.entries(review.reviewedFiles ?? {})) m.set(k, new Set(v));
    return m;
  }, [review.reviewedFiles]);

  const isReviewed = useCallback((key: string, path: string) => reviewedSets.get(key)?.has(path) ?? false, [reviewedSets]);

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
      if (updated) setReview((r) => ({ ...r, reviewedFiles: updated.reviewedFiles, updatedAt: updated.updatedAt }));
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
    const updated = await track(() => api.refreshReview(reviewId));
    if (!updated) return;
    pendingFiles.current.clear();
    setFilesByKey({});
    setReview(updated);
    setUiState((prev) => {
      const ok = prev.commitKey === RANGE || updated.commits.some((c) => c.sha === prev.commitKey);
      return ok ? prev : { ...prev, commitKey: updated.commits.find((c) => !c.outdated)?.sha ?? RANGE, currentFile: null };
    });
  }, [reviewId, track]);

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
