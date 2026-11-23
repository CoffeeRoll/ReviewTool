import { useEffect, useMemo, useState } from 'react';
import { api } from '../lib/api';
import { basename } from '../lib/diff';
import { highlightFile, highlightLine, languageFor } from '../lib/highlight';
import { useSession, type JumpOrigin } from '../lib/session';
import { RANGE, type FileDiff, type FinalLocation, type LaterChange } from '../lib/types';
import { DiffView } from './DiffView';

/** Most final-version lines shown for one peek (the rest is a click away in the combined diff). */
const MAX_REGION_LINES = 40;
const CONTEXT = 3;

/**
 * Inline preview of how a later commit changes a line, and what the line ends up as in the final
 * version, without leaving the commit being reviewed.
 */
export function LaterChangePeek({
  change,
  fromCommit,
  path,
  line,
  viewId,
  onClose,
}: {
  change: LaterChange;
  fromCommit: string;
  path: string;
  line: number;
  viewId: string;
  onClose: () => void;
}) {
  const { review } = useSession();
  const [tab, setTab] = useState<'commit' | 'final'>('commit');
  const active = review.commits.filter((c) => !c.outdated);
  const number = active.findIndex((c) => c.sha === change.commitSha) + 1;
  const subject = active[number - 1]?.subject ?? '';
  const from: JumpOrigin = { commitKey: fromCommit, path, side: 'New', line };

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className="peek" role="region" aria-label={`Peek at commit ${number}`}>
      <div className="peek-head">
        <span className="peek-title">
          ↻ Line {line} changes again in <strong>commit {number}</strong> <span className="muted">· {subject}</span>
        </span>
        <span className="spacer" />
        <div className="seg">
          <button className={tab === 'commit' ? 'on' : ''} onClick={() => setTab('commit')} title="The part of that commit that changes this line">
            Commit {number}
          </button>
          <button className={tab === 'final' ? 'on' : ''} onClick={() => setTab('final')} title="What this code looks like at the end of the review">
            Final version
          </button>
        </div>
        <button className="icon-btn" onClick={onClose} aria-label="Close peek" title="Close (Esc)">
          ×
        </button>
      </div>
      {tab === 'commit' ? (
        <CommitTab change={change} path={path} viewId={viewId} from={from} number={number} />
      ) : (
        <FinalTab fromCommit={fromCommit} path={path} line={line} from={from} />
      )}
    </div>
  );
}

function CommitTab({ change, path, viewId, from, number }: { change: LaterChange; path: string; viewId: string; from: JumpOrigin; number: number }) {
  const { getDiff, jumpTo } = useSession();
  const [diff, setDiff] = useState<FileDiff | 'missing' | null>(null);

  useEffect(() => {
    let cancelled = false;
    getDiff(change.commitSha, path, null, CONTEXT).then(
      (d) => {
        const hunk = d.hunks.find((h) => h.lines.some((l) => l.type !== 'Add' && l.oldLine === change.oldLine));
        if (!cancelled) setDiff(hunk ? { ...d, hunks: [hunk] } : 'missing');
      },
      () => !cancelled && setDiff('missing'),
    );
    return () => {
      cancelled = true;
    };
  }, [getDiff, change.commitSha, change.oldLine, path]);

  return (
    <>
      <div className="peek-body">
        {diff === null ? (
          <div className="diff-message muted">Loading…</div>
        ) : diff === 'missing' ? (
          <div className="diff-message">Couldn't find this line in commit {number}'s changes (the file may have been renamed).</div>
        ) : (
          <DiffView viewId={`${viewId}>peek`} commitKey={change.commitSha} path={path} diff={diff} compact focus={{ side: 'Old', line: change.oldLine }} />
        )}
      </div>
      <div className="peek-foot">
        <span className="muted small">Lines that change again in yet another commit have their own ↻ markers — click to keep following.</span>
        <span className="spacer" />
        <button className="btn small" onClick={() => jumpTo({ commitKey: change.commitSha, path, side: 'Old', line: change.oldLine }, from)}>
          Open commit {number} →
        </button>
      </div>
    </>
  );
}

function FinalTab({ fromCommit, path, line, from }: { fromCommit: string; path: string; line: number; from: JumpOrigin }) {
  const { review, getBlob, jumpTo } = useSession();
  const [state, setState] = useState<{ loc: FinalLocation; text: string } | 'gone' | 'error' | null>(null);
  const lang = useMemo(() => languageFor(path), [path]);

  useEffect(() => {
    let cancelled = false;
    api
      .finalLocation(review.id, fromCommit, path, line)
      .then(async (loc) => {
        const blob = await getBlob(RANGE, loc.path, 'New');
        if (!cancelled) setState(blob.text == null ? 'error' : { loc, text: blob.text });
      })
      .catch((e: { status?: number }) => !cancelled && setState(e.status === 404 ? 'gone' : 'error'));
    return () => {
      cancelled = true;
    };
  }, [review.id, fromCommit, path, line, getBlob]);

  const view = useMemo(() => {
    if (!state || typeof state === 'string') return null;
    const { loc, text } = state;
    const file = highlightFile(text, lang, `${RANGE}|New|${loc.path}|${review.headSha}`);
    const lines = file?.lines ?? text.replace(/\r\n/g, '\n').replace(/\n$/, '').split('\n');
    const removed = loc.endLine < loc.startLine;
    const regionEnd = removed ? loc.startLine - 1 : Math.min(loc.endLine, lines.length, loc.startLine + MAX_REGION_LINES - 1);
    const truncated = !removed && Math.min(loc.endLine, lines.length) > regionEnd;
    const first = Math.max(1, loc.startLine - CONTEXT);
    const last = Math.min(lines.length, regionEnd + (truncated ? 0 : CONTEXT));
    const rows = [];
    for (let n = first; n <= last; n++) {
      rows.push({ n, html: file?.html[n - 1] ?? highlightLine(lines[n - 1], lang), inRegion: !removed && n >= loc.startLine && n <= regionEnd });
    }
    return { loc, rows, removed, removedAfter: loc.startLine - 1, truncated };
  }, [state, lang, review.headSha]);

  return (
    <>
      <div className="peek-body">
        {state === null ? (
          <div className="diff-message muted">Loading…</div>
        ) : state === 'gone' ? (
          <div className="diff-message">{basename(path)} doesn't exist in the final version.</div>
        ) : state === 'error' || !view ? (
          <div className="diff-message error">Couldn't load the final version of this file.</div>
        ) : (
          <>
            <div className="peek-caption small muted">
              {view.loc.unchanged
                ? `Unchanged at the end of the review — now line ${view.loc.startLine}.`
                : view.removed
                  ? 'Removed by the end of the review, with nothing in its place.'
                  : `At the end of the review this is replaced by ${view.rows.filter((r) => r.inRegion).length}${view.truncated ? '+' : ''} line(s), highlighted below.`}
            </div>
            <table className="diff-table plain final-table">
              <colgroup>
                <col className="ln-col" />
                <col />
              </colgroup>
              <tbody>
                {view.rows.map((r) => (
                  <FinalRow key={r.n} n={r.n} html={r.html} inRegion={r.inRegion} removedMarker={view.removed && r.n === view.removedAfter} />
                ))}
                {view.removed && view.removedAfter < 1 && <RemovedRow />}
                {view.truncated && (
                  <tr className="hunk-row">
                    <td colSpan={2}>⋯ more — open the combined diff to see it all</td>
                  </tr>
                )}
              </tbody>
            </table>
          </>
        )}
      </div>
      <div className="peek-foot">
        <span className="spacer" />
        {view && (
          <button
            className="btn small"
            onClick={() =>
              jumpTo({ commitKey: RANGE, path: view.loc.path, side: 'New', line: Math.max(1, view.removed ? view.removedAfter : view.loc.startLine) }, from)
            }
          >
            Open in the combined diff →
          </button>
        )}
      </div>
    </>
  );
}

function FinalRow({ n, html, inRegion, removedMarker }: { n: number; html: string; inRegion: boolean; removedMarker: boolean }) {
  return (
    <>
      <tr className={`line context ${inRegion ? 'final-region' : ''}`}>
        <td className="ln">{n}</td>
        <td className="code">
          <span className="src" dangerouslySetInnerHTML={{ __html: html || ' ' }} />
        </td>
      </tr>
      {removedMarker && <RemovedRow />}
    </>
  );
}

function RemovedRow() {
  return (
    <tr className="removed-marker">
      <td className="ln" />
      <td className="code">— the line was here; it's gone in the final version —</td>
    </tr>
  );
}
