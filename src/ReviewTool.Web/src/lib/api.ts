import type {
  BlobContent,
  ChangedFile,
  CommitInfo,
  CreateReviewRequest,
  DiffSide,
  FileDiff,
  FileHistoryEntry,
  FinalLocation,
  LaterChange,
  MovedBlock,
  NoteFollowUp,
  OpenPullRequestList,
  ReviewPullRequestLink,
  ReviewPullRequestStatus,
  PublishPreview,
  PublishRequestBody,
  PublishResult,
  PublishTarget,
  NoteInput,
  RepoInfo,
  RepoRefs,
  Review,
  ReviewNote,
  ReviewStatus,
  ReviewSummary,
  UiState,
} from './types';

/** Base URL of the API. Override with VITE_API_BASE when embedding the UI elsewhere. */
const BASE = (import.meta.env.VITE_API_BASE as string | undefined) ?? 'api';

export class ApiError extends Error {
  constructor(message: string, public status: number) {
    super(message);
  }
}

async function request<T>(method: string, url: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const res = await fetch(`${BASE}${url}`, {
    method,
    headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
    body: body !== undefined ? JSON.stringify(body) : undefined,
    signal,
  });
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const problem = await res.json();
      message = problem.detail || problem.title || message;
    } catch {
      /* not JSON */
    }
    throw new ApiError(message, res.status);
  }
  if (res.status === 204) return undefined as T;
  const type = res.headers.get('content-type') ?? '';
  return (type.includes('json') ? await res.json() : await res.text()) as T;
}

const q = (params: Record<string, string | number | boolean | null | undefined>) => {
  const s = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== '') s.set(k, String(v));
  const str = s.toString();
  return str ? `?${str}` : '';
};

const enc = encodeURIComponent;

export const api = {
  repoInfo: (path: string) => request<RepoInfo>('GET', `/repo${q({ path })}`),
  repoRefs: (path: string) => request<RepoRefs>('GET', `/repo/refs${q({ path })}`),
  openPullRequests: (path: string, remote?: string | null) => request<OpenPullRequestList>('GET', `/repo/pulls${q({ path, remote })}`),
  /** State ("open" | "merged" | "closed") of the PR behind each review started from a PR, keyed by review id. */
  reviewPullRequestStates: () => request<Record<string, ReviewPullRequestStatus>>('GET', '/reviews/pr-states'),
  /** The review's pull request on the hosting site (undefined when the repo isn't on a supported platform). */
  pullRequestLink: (id: string) => request<ReviewPullRequestLink | undefined>('GET', `/reviews/${enc(id)}/pull-request-link`),
  /** Resumes the matching review or starts a new one. */
  openReview: (req: CreateReviewRequest) => request<{ review: Review; created: boolean }>('POST', '/reviews/open', req),
  repoCommits: (path: string, base: string, head: string, signal?: AbortSignal) =>
    request<CommitInfo[]>('GET', `/repo/commits${q({ path, base, head })}`, undefined, signal),

  /** Server-side settings the UI needs (e.g. extra file-type → language mappings). */
  settings: () => request<{ languages: Record<string, string> }>('GET', '/settings'),
  listReviews: () => request<ReviewSummary[]>('GET', '/reviews'),
  createReview: (req: CreateReviewRequest) => request<Review>('POST', '/reviews', req),
  getReview: (id: string) => request<Review>('GET', `/reviews/${enc(id)}`),
  patchReview: (id: string, patch: { name?: string; summary?: string; status?: ReviewStatus; uiState?: UiState }) =>
    request<Review>('PATCH', `/reviews/${enc(id)}`, patch),
  deleteReview: (id: string) => request<void>('DELETE', `/reviews/${enc(id)}`),
  refreshReview: (id: string) => request<Review>('POST', `/reviews/${enc(id)}/refresh`),

  changedFiles: (id: string, commit: string, ignoreWhitespace = false) =>
    request<ChangedFile[]>('GET', `/reviews/${enc(id)}/commits/${enc(commit)}/files${q({ ignoreWhitespace: ignoreWhitespace || undefined })}`),
  fileDiff: (id: string, commit: string, path: string, oldPath: string | null | undefined, context: number, ignoreWhitespace = false) =>
    request<FileDiff>(
      'GET',
      `/reviews/${enc(id)}/commits/${enc(commit)}/diff${q({ path, oldPath, context, ignoreWhitespace: ignoreWhitespace || undefined })}`,
    ),
  moved: (id: string, commit: string, ignoreWhitespace = false) =>
    request<MovedBlock[]>('GET', `/reviews/${enc(id)}/commits/${enc(commit)}/moved${q({ ignoreWhitespace: ignoreWhitespace || undefined })}`),
  laterChanges: (id: string, commit: string, path: string) =>
    request<LaterChange[]>('GET', `/reviews/${enc(id)}/commits/${enc(commit)}/later-changes${q({ path })}`),
  finalLocation: (id: string, commit: string, path: string, line: number, endLine?: number) =>
    request<FinalLocation>('GET', `/reviews/${enc(id)}/commits/${enc(commit)}/final-location${q({ path, line, endLine })}`),
  followUps: (id: string) => request<NoteFollowUp[]>('GET', `/reviews/${enc(id)}/followups`),
  fileHistory: (id: string, path: string) => request<FileHistoryEntry[]>('GET', `/reviews/${enc(id)}/file-history${q({ path })}`),
  setLinesReviewed: (id: string, commit: string, path: string, ranges: string[], reviewed: boolean) =>
    request<Review>('PUT', `/reviews/${enc(id)}/commits/${enc(commit)}/reviewed-lines`, { path, ranges, reviewed }),
  tree: (id: string, commit: string) => request<string[]>('GET', `/reviews/${enc(id)}/commits/${enc(commit)}/tree`),
  blob: (id: string, commit: string, path: string, side: DiffSide = 'New') =>
    request<BlobContent>('GET', `/reviews/${enc(id)}/commits/${enc(commit)}/blob${q({ path, side })}`),
  setReviewed: (id: string, commit: string, paths: string[], reviewed: boolean) =>
    request<Review>('PUT', `/reviews/${enc(id)}/commits/${enc(commit)}/reviewed`, { paths, reviewed }),

  addNote: (id: string, note: NoteInput) => request<ReviewNote>('POST', `/reviews/${enc(id)}/notes`, note),
  updateNote: (id: string, noteId: string, note: NoteInput) =>
    request<ReviewNote>('PATCH', `/reviews/${enc(id)}/notes/${enc(noteId)}`, note),
  deleteNote: (id: string, noteId: string) => request<void>('DELETE', `/reviews/${enc(id)}/notes/${enc(noteId)}`),

  exportMarkdown: (id: string, includeResolved = true) =>
    request<string>('GET', `/reviews/${enc(id)}/export.md${q({ includeResolved })}`),
  publishTargets: (id: string) => request<PublishTarget[]>('GET', `/reviews/${enc(id)}/publish/targets`),
  publishPreview: (id: string, body: PublishRequestBody, signal?: AbortSignal) =>
    request<PublishPreview>('POST', `/reviews/${enc(id)}/publish/preview`, body, signal),
  publish: (id: string, body: PublishRequestBody) => request<PublishResult>('POST', `/reviews/${enc(id)}/publish`, body),

  exportUrl: (id: string) => `${BASE}/reviews/${enc(id)}/export.md`,

  /** Fire-and-forget save that survives page unload. */
  saveUiKeepalive: (id: string, uiState: UiState) => {
    fetch(`${BASE}/reviews/${enc(id)}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ uiState }),
      keepalive: true,
    }).catch(() => undefined);
  },
};
