import type { DiffHunk, DiffLine, DiffSide, FileDiff, ReviewCommit, ReviewNote } from './types';

/** Where a note can be attached for a given diff line. */
export interface LineAnchor {
  side: DiffSide;
  line: number;
}

export function anchorFor(line: DiffLine): LineAnchor {
  if (line.type === 'Delete') return { side: 'Old', line: line.oldLine! };
  return { side: 'New', line: line.newLine! };
}

export const anchorKey = (a: LineAnchor) => `${a.side}:${a.line}`;

/** Notes are rendered beneath the last line they cover. */
export function noteAnchorKey(n: ReviewNote): string | null {
  if (n.line == null) return null;
  return `${n.side}:${n.endLine ?? n.line}`;
}

export interface SplitRow {
  left: DiffLine | null;
  right: DiffLine | null;
}

/**
 * Pairs a hunk's lines for side-by-side display: context lines appear on both
 * sides; a run of deletions followed by additions is zipped together.
 */
export function toSplitRows(lines: DiffLine[]): SplitRow[] {
  const rows: SplitRow[] = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (line.type === 'Context') {
      rows.push({ left: line, right: line });
      i++;
      continue;
    }
    const dels: DiffLine[] = [];
    const adds: DiffLine[] = [];
    while (i < lines.length && lines[i].type === 'Delete') dels.push(lines[i++]);
    while (i < lines.length && lines[i].type === 'Add') adds.push(lines[i++]);
    const n = Math.max(dels.length, adds.length);
    for (let k = 0; k < n; k++) rows.push({ left: dels[k] ?? null, right: adds[k] ?? null });
  }
  return rows;
}

/** Number of unchanged lines hidden before a hunk (relative to the previous one). */
export function hiddenLinesBefore(hunks: DiffHunk[], index: number): number {
  const hunk = hunks[index];
  if (index === 0) return Math.max(0, hunk.newStart - 1);
  const prev = hunks[index - 1];
  const prevEnd = prev.newStart + prev.newCount;
  return Math.max(0, hunk.newStart - prevEnd);
}

/** Collects the text of lines [from, to] on one side of a diff (for note snapshots). */
export function linesText(diff: FileDiff, side: DiffSide, from: number, to: number): string {
  const out: string[] = [];
  for (const h of diff.hunks) {
    for (const l of h.lines) {
      const n = side === 'Old' ? (l.type !== 'Add' ? l.oldLine : null) : l.type !== 'Delete' ? l.newLine : null;
      if (n != null && n >= from && n <= to) out.push(l.text);
    }
  }
  return out.join('\n');
}

export function shortSha(sha: string) {
  return sha.slice(0, 8);
}

export interface CommitProgress {
  reviewed: number;
  total: number;
  done: boolean;
}

export function commitProgress(reviewedFiles: Record<string, string[]>, key: string, total: number): CommitProgress {
  const reviewed = Math.min(reviewedFiles[key]?.length ?? 0, total);
  return { reviewed, total, done: reviewed >= total };
}

export function overallProgress(commits: ReviewCommit[], reviewedFiles: Record<string, string[]>) {
  let reviewed = 0;
  let total = 0;
  let commitsDone = 0;
  const active = commits.filter((c) => !c.outdated);
  for (const c of active) {
    const p = commitProgress(reviewedFiles, c.sha, c.fileCount);
    reviewed += p.reviewed;
    total += p.total;
    if (p.done) commitsDone++;
  }
  return { reviewed, total, commitsDone, commitCount: active.length, ratio: total === 0 ? 1 : reviewed / total };
}

export function basename(path: string) {
  const i = path.lastIndexOf('/');
  return i >= 0 ? path.slice(i + 1) : path;
}

export function dirname(path: string) {
  const i = path.lastIndexOf('/');
  return i >= 0 ? path.slice(0, i) : '';
}

export function relativeTime(iso: string): string {
  const then = new Date(iso).getTime();
  const diff = (Date.now() - then) / 1000;
  if (diff < 60) return 'just now';
  if (diff < 3600) return `${Math.floor(diff / 60)}m ago`;
  if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`;
  if (diff < 86400 * 30) return `${Math.floor(diff / 86400)}d ago`;
  return new Date(iso).toLocaleDateString();
}
