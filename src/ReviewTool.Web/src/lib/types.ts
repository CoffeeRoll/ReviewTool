export const RANGE = 'range';

export type ChangeKind = 'Modified' | 'Added' | 'Deleted' | 'Renamed' | 'Copied' | 'TypeChanged' | 'Unknown';
export type DiffLineType = 'Context' | 'Add' | 'Delete';
export type DiffSide = 'New' | 'Old';
export type NoteKind = 'Comment' | 'Question' | 'Issue' | 'Suggestion' | 'Nit' | 'Praise';
export type ReviewStatus = 'InProgress' | 'Approved' | 'ChangesRequested' | 'Commented';

export const NOTE_KINDS: NoteKind[] = ['Comment', 'Question', 'Issue', 'Suggestion', 'Nit', 'Praise'];
export const REVIEW_STATUSES: { value: ReviewStatus; label: string }[] = [
  { value: 'InProgress', label: 'In progress' },
  { value: 'Commented', label: 'Commented' },
  { value: 'ChangesRequested', label: 'Changes requested' },
  { value: 'Approved', label: 'Approved' },
];

export interface CommitInfo {
  sha: string;
  parents: string[];
  authorName: string;
  authorEmail: string;
  authorDate: string;
  committerName: string;
  commitDate: string;
  subject: string;
  body: string;
}

export interface ChangedFile {
  path: string;
  oldPath: string | null;
  status: ChangeKind;
  additions: number;
  deletions: number;
  isBinary: boolean;
  similarity: number | null;
}

export interface DiffLine {
  type: DiffLineType;
  oldLine: number | null;
  newLine: number | null;
  text: string;
  noNewlineAtEnd: boolean;
}

export interface DiffHunk {
  oldStart: number;
  oldCount: number;
  newStart: number;
  newCount: number;
  header: string;
  lines: DiffLine[];
}

export interface FileDiff {
  path: string;
  oldPath: string | null;
  status: ChangeKind;
  isBinary: boolean;
  oldMode: string | null;
  newMode: string | null;
  hunks: DiffHunk[];
  truncated: boolean;
}

export interface BlobContent {
  path: string;
  sha: string;
  isBinary: boolean;
  truncated: boolean;
  size: number;
  text: string | null;
}

export interface RefInfo {
  name: string;
  kind: 'branch' | 'remote' | 'tag' | 'other';
  sha: string;
}

export interface RepoRefs {
  currentBranch: string | null;
  defaultBranch: string | null;
  refs: RefInfo[];
}

export interface RepoInfo {
  path: string;
  currentBranch: string | null;
  remotes: string[];
}

export interface PullRequestRef {
  remote: string;
  number: number;
  provider: string;
}

export interface ReviewCommit {
  sha: string;
  parents: string[];
  diffBase: string;
  subject: string;
  body: string;
  authorName: string;
  authorEmail: string;
  authorDate: string;
  fileCount: number;
  additions: number;
  deletions: number;
  isMerge: boolean;
  outdated: boolean;
}

export interface ReviewNote {
  id: string;
  commitSha: string;
  filePath?: string | null;
  side: DiffSide;
  line?: number | null;
  endLine?: number | null;
  lineText?: string | null;
  body: string;
  kind: NoteKind;
  resolved: boolean;
  createdAt: string;
  updatedAt: string;
}

export type Layout = 'stack' | 'panes';
export type DiffStyle = 'unified' | 'split';

export interface PaneSpec {
  id: string;
  path: string;
  oldPath?: string | null;
  /** 'diff' shows changes; 'file' shows the whole file at the commit. */
  mode: 'diff' | 'file';
  /** When set the pane stays on this commit instead of following the current one. */
  pinnedCommit?: string | null;
}

export interface UiState {
  commitKey?: string;
  layout?: Layout;
  diffStyle?: DiffStyle;
  context?: number;
  wrap?: boolean;
  panes?: PaneSpec[];
  activePane?: string;
  notesOpen?: boolean;
  currentFile?: string | null;
  sidebarWidth?: number;
}

export interface Review {
  id: string;
  name: string;
  repoPath: string;
  baseRef: string;
  headRef: string;
  baseSha: string;
  mergeBaseSha: string;
  headSha: string;
  pullRequest?: PullRequestRef | null;
  createdAt: string;
  updatedAt: string;
  refreshedAt?: string | null;
  status: ReviewStatus;
  summary: string;
  commits: ReviewCommit[];
  reviewedFiles: Record<string, string[]>;
  notes: ReviewNote[];
  uiState?: UiState | null;
}

export interface ReviewSummary {
  id: string;
  name: string;
  repoPath: string;
  baseRef: string;
  headRef: string;
  headSha: string;
  status: ReviewStatus;
  createdAt: string;
  updatedAt: string;
  commitCount: number;
  reviewedCommitCount: number;
  fileCount: number;
  reviewedFileCount: number;
  noteCount: number;
  openNoteCount: number;
  pullRequest?: PullRequestRef | null;
}

export interface NoteInput {
  commitSha?: string;
  filePath?: string | null;
  side?: DiffSide;
  line?: number | null;
  endLine?: number | null;
  lineText?: string | null;
  body?: string;
  kind?: NoteKind;
  resolved?: boolean;
}

export interface CreateReviewRequest {
  repoPath: string;
  baseRef?: string;
  headRef?: string;
  name?: string;
  pullRequest?: { number: number; remote?: string; provider?: string };
}
