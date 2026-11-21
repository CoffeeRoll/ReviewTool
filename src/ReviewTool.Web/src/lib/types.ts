export const RANGE = 'range';
export const SINCE = 'since';
export const isSpecialKey = (k: string) => k === RANGE || k === SINCE;

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
  isGenerated: boolean;
}

export interface MovedBlock {
  fromPath: string;
  fromStart: number;
  fromEnd: number;
  toPath: string;
  toStart: number;
  toEnd: number;
}

/** A line (new side of a commit) that a later commit changes; oldLine is its number in that commit's diff. */
export interface LaterChange {
  line: number;
  commitSha: string;
  oldLine: number;
}

export interface NoteFollowUp {
  noteId: string;
  commitSha: string;
  path: string;
  line: number;
  endLine: number;
  changedLines: number;
  totalLines: number;
}

export interface FileHistoryEntry {
  sha: string;
  path: string;
  oldPath: string | null;
  status: ChangeKind;
}

export type CommitChange = 'Unchanged' | 'Modified' | 'New';

export interface ReviewSnapshot {
  headSha: string;
  mergeBaseSha: string;
  baseSha: string;
  recordedAt: string;
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
  sinceLastReview?: CommitChange | null;
  previousSha?: string | null;
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
  /** 'diff' shows changes; 'file' the whole file at the commit; 'history' steps through every commit touching the file. */
  mode: 'diff' | 'file' | 'history';
  /** When set the pane stays on this commit instead of following the current one. */
  pinnedCommit?: string | null;
  /** History mode: the commit currently selected on the file's timeline. */
  historyCommit?: string | null;
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
  ignoreWhitespace?: boolean;
  fileTree?: boolean;
  /** Mark (and dim) added lines that a later commit rewrites. */
  showLater?: boolean;
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
  /** commit key → path → reviewed changed-line ranges ("N12-20", "O5"). */
  reviewedLines?: Record<string, Record<string, string[]>> | null;
  previousHead?: ReviewSnapshot | null;
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
