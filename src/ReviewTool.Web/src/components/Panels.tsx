import { useMemo, useState } from 'react';
import { basename, commitProgress, shortSha } from '../lib/diff';
import { notesFor, useSession } from '../lib/session';
import { RANGE, SINCE, isSpecialKey, type ReviewNote } from '../lib/types';
import { KindBadge, NoteThread } from './Notes';

/** Previous/next commit for navigation; the combined view sits before the first commit. */
export function neighbours(keys: string[], key: string): { prev: string | null; next: string | null } {
  if (key === SINCE || key === RANGE) return { prev: null, next: keys[0] ?? null };
  const i = keys.indexOf(key);
  if (i < 0) return { prev: null, next: keys[0] ?? null };
  return { prev: i > 0 ? keys[i - 1] : RANGE, next: i < keys.length - 1 ? keys[i + 1] : null };
}

export function CommitHeader({ onMarkAndNext }: { onMarkAndNext: () => void }) {
  const { review, ui, commitKeys, selectCommit, files, draft, setDraft } = useSession();
  const [showBody, setShowBody] = useState(true);
  const key = ui.commitKey;
  const idx = commitKeys.indexOf(key);
  const commit = isSpecialKey(key) ? null : review.commits.find((c) => c.sha === key);
  const changedCommits = review.commits.filter((c) => !c.outdated && (c.sinceLastReview === 'New' || c.sinceLastReview === 'Modified'));
  const fileList = files(key);
  const progress = commitProgress(review.reviewedFiles, key, commit?.fileCount ?? fileList?.length ?? 0);
  const commitNotes = notesFor(review, key, null);
  const draftHere = draft && draft.commitKey === key && draft.path === null ? draft : null;

  const { prev, next } = neighbours(commitKeys, key);

  return (
    <div className="commit-header">
      <div className="commit-header-row">
        <div className="commit-title">
          {commit ? (
            <>
              <div className="eyebrow">
                Commit {idx >= 0 ? `${idx + 1} of ${commitKeys.length}` : '(no longer in range)'}
                {commit.isMerge && <span className="pill">merge · diff vs first parent</span>}
              </div>
              <h2>{commit.subject || '(no message)'}</h2>
              <div className="muted small">
                <span className="mono copyable" title="Click to copy SHA" onClick={() => navigator.clipboard?.writeText(commit.sha)}>
                  {shortSha(commit.sha)}
                </span>{' '}
                · {commit.authorName} &lt;{commit.authorEmail}&gt; · {new Date(commit.authorDate).toLocaleString()} ·{' '}
                <span className="add">+{commit.additions}</span> <span className="del">−{commit.deletions}</span> in {commit.fileCount} file
                {commit.fileCount === 1 ? '' : 's'}
              </div>
            </>
          ) : key === SINCE && review.previousHead ? (
            <>
              <div className="eyebrow">Since your last review</div>
              <h2>What changed since {new Date(review.previousHead.recordedAt).toLocaleString()}</h2>
              <div className="muted small">
                <span className="mono">{shortSha(review.previousHead.headSha)}</span> → <span className="mono">{shortSha(review.headSha)}</span>
                {fileList ? ` · ${fileList.length} files` : ''} · only files this review touches (changes that came from the base branch
                are left out)
              </div>
              {changedCommits.length > 0 ? (
                <div className="since-commits">
                  {changedCommits.map((c) => (
                    <button key={c.sha} className="chip" onClick={() => selectCommit(c.sha)} title={c.subject}>
                      <span className={`change-badge change-${c.sinceLastReview!.toLowerCase()}`}>{c.sinceLastReview === 'New' ? 'new' : 'changed'}</span>
                      {c.subject}
                    </button>
                  ))}
                </div>
              ) : (
                <div className="muted small">No commits were added or rewritten — the branch was only rebased.</div>
              )}
            </>
          ) : (
            <>
              <div className="eyebrow">Combined diff</div>
              <h2>All changes in this review</h2>
              <div className="muted small">
                <span className="mono">{review.baseRef}</span> ({shortSha(review.mergeBaseSha)}) → <span className="mono">{review.headRef}</span> (
                {shortSha(review.headSha)}) · {commitKeys.length} commits{fileList ? ` · ${fileList.length} files` : ''}
              </div>
            </>
          )}
        </div>
        <div className="commit-nav">
          <button className="btn" disabled={!prev} onClick={() => prev && selectCommit(prev)} title="Previous commit (p)">
            ← Prev
          </button>
          <button className="btn" disabled={!next} onClick={() => next && selectCommit(next)} title="Next commit (n)">
            Next →
          </button>
          <button className="btn primary" onClick={onMarkAndNext} title="Mark every file reviewed and go to the next commit (Shift+M)">
            {progress.done ? '✓ Reviewed' : 'Mark reviewed'}
            {next ? ' & next' : ''}
          </button>
        </div>
      </div>
      {commit?.body && (
        <div className="commit-body">
          <button className="btn-link small" onClick={() => setShowBody((s) => !s)}>
            {showBody ? 'Hide description' : 'Show description'}
          </button>
          {showBody && <pre>{commit.body}</pre>}
        </div>
      )}
      <div className="commit-notes">
        {(commitNotes.length > 0 || draftHere) && <NoteThread notes={commitNotes} draft={draftHere} />}
        {!draftHere && (
          <button
            className="btn-link small"
            onClick={() => setDraft({ commitKey: key, path: null, side: 'New', line: null, endLine: null, viewId: 'commit' })}
          >
            💬 Add a note on this {commit ? 'commit' : 'review'}
          </button>
        )}
      </div>
    </div>
  );
}

type NoteFilter = 'open' | 'all' | 'here';

export function NotesPanel({ onClose, onExport }: { onClose: () => void; onExport: () => void }) {
  const { review, ui, jumpTo, followUps } = useSession();
  const [filter, setFilter] = useState<NoteFilter>('open');
  const followUpByNote = useMemo(() => new Map(followUps.map((f) => [f.noteId, f])), [followUps]);
  const commitNumber = (sha: string) => review.commits.filter((c) => !c.outdated).findIndex((c) => c.sha === sha) + 1;

  const groups = useMemo(() => {
    const order = new Map(review.commits.map((c, i) => [c.sha, i]));
    const notes = review.notes.filter((n) =>
      filter === 'open' ? !n.resolved : filter === 'here' ? n.commitSha === ui.commitKey : true,
    );
    const by = new Map<string, ReviewNote[]>();
    for (const n of notes) by.set(n.commitSha, [...(by.get(n.commitSha) ?? []), n]);
    return [...by.entries()]
      .sort(([a], [b]) => (a === RANGE ? 1e9 : order.get(a) ?? 1e8) - (b === RANGE ? 1e9 : order.get(b) ?? 1e8))
      .map(([sha, list]) => ({
        sha,
        commit: review.commits.find((c) => c.sha === sha),
        notes: list.sort((x, y) => (x.filePath ?? '').localeCompare(y.filePath ?? '') || (x.line ?? 0) - (y.line ?? 0)),
      }));
  }, [review.notes, review.commits, filter, ui.commitKey]);

  const openCount = review.notes.filter((n) => !n.resolved).length;

  return (
    <aside className="notes-panel">
      <div className="notes-panel-head">
        <strong>Notes</strong>
        <span className="muted small">
          {openCount} open · {review.notes.length} total
        </span>
        <span className="spacer" />
        <button className="icon-btn" onClick={onClose} aria-label="Close notes">
          ×
        </button>
      </div>
      <div className="seg full">
        {(['open', 'all', 'here'] as NoteFilter[]).map((f) => (
          <button key={f} className={filter === f ? 'on' : ''} onClick={() => setFilter(f)}>
            {f === 'open' ? 'Open' : f === 'all' ? 'All' : 'This commit'}
          </button>
        ))}
      </div>
      <div className="notes-panel-list">
        {groups.length === 0 && (
          <p className="muted small pad">
            No notes yet. Hover a line and click <strong>+</strong> to add one.
          </p>
        )}
        {groups.map((g) => (
          <div key={g.sha} className="notes-group">
            <div className="notes-group-title">
              {g.sha === RANGE ? 'All changes' : g.sha === SINCE ? 'Since last review' : <><span className="mono">{shortSha(g.sha)}</span> {g.commit?.subject}</>}
              {g.commit?.outdated && <span className="pill warn">outdated</span>}
            </div>
            {g.notes.map((n) => (
              <button
                key={n.id}
                className={`note-link ${n.resolved ? 'resolved' : ''}`}
                onClick={() => jumpTo({ commitKey: n.commitSha, path: n.filePath ?? null, side: n.side, line: n.endLine ?? n.line })}
              >
                <span className="note-link-head">
                  <KindBadge kind={n.kind} />
                  <span className="mono small">
                    {n.filePath ? `${basename(n.filePath)}${n.line ? `:${n.line}${n.endLine ? `-${n.endLine}` : ''}` : ''}` : 'commit note'}
                  </span>
                  {n.resolved && <span className="muted small">resolved</span>}
                  {followUpByNote.has(n.id) && (
                    <span className="pill later-pill" title="A later commit changes the code this note is about">
                      ↻ changed in {commitNumber(followUpByNote.get(n.id)!.commitSha)}
                    </span>
                  )}
                </span>
                <span className="note-link-body">{n.body}</span>
              </button>
            ))}
          </div>
        ))}
      </div>
      <div className="notes-panel-foot">
        <button className="btn" onClick={onExport}>
          Export / finish review…
        </button>
      </div>
    </aside>
  );
}
