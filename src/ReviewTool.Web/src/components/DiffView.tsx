import { Fragment, useEffect, useMemo, useRef, type ReactNode } from 'react';
import { anchorFor, anchorKey, hiddenLinesBefore, linesText, noteAnchorKey, toSplitRows, type LineAnchor } from '../lib/diff';
import { highlightLine, languageFor } from '../lib/highlight';
import { notesFor, useSession, type NoteDraft } from '../lib/session';
import type { DiffHunk, DiffLine, FileDiff, ReviewNote } from '../lib/types';
import { NoteCard, NoteThread } from './Notes';

/** Jumps already scrolled to, so remounting a view doesn't scroll again. */
const handledJumps = new Set<number>();

interface Props {
  /** Unique id of the place this view is rendered (so only one copy shows the draft editor). */
  viewId: string;
  commitKey: string;
  path: string;
  diff: FileDiff;
  /** True when the diff was requested with full context (no hidden lines). */
  isFull?: boolean;
  onExpand?: () => void;
  /** Plain file view: a single line-number column and no +/- markers. */
  plain?: boolean;
  /** The diff shown is from a previous request (a newer one is loading). */
  stale?: boolean;
}

export function DiffView({ viewId, commitKey, path, diff, isFull, onExpand, plain, stale }: Props) {
  const { review, ui, draft, setDraft, jump } = useSession();
  const lang = useMemo(() => languageFor(path), [path]);
  const tableRef = useRef<HTMLTableElement>(null);
  const split = ui.diffStyle === 'split' && !plain;

  const notes = useMemo(() => notesFor(review, commitKey, path), [review, commitKey, path]);

  // Index notes by the line they render under; anything we can't place goes in the header.
  const { byAnchor, unplaced } = useMemo(() => {
    const present = new Set<string>();
    for (const h of diff.hunks) for (const l of h.lines) present.add(anchorKey(anchorFor(l)));
    const map = new Map<string, ReviewNote[]>();
    const rest: ReviewNote[] = [];
    for (const n of notes) {
      const key = noteAnchorKey(n);
      if (key && present.has(key)) map.set(key, [...(map.get(key) ?? []), n]);
      else rest.push(n);
    }
    return { byAnchor: map, unplaced: rest };
  }, [diff, notes]);

  const myDraft = draft && draft.viewId === viewId && draft.commitKey === commitKey && draft.path === path ? draft : null;
  const draftKey = myDraft && myDraft.line != null ? `${myDraft.side}:${myDraft.endLine ?? myDraft.line}` : null;
  const inDraft = (a: LineAnchor | null) =>
    !!(a && myDraft && myDraft.line != null && a.side === myDraft.side && a.line >= myDraft.line && a.line <= (myDraft.endLine ?? myDraft.line));

  const onGutterClick = (anchor: LineAnchor, extend: boolean) => {
    if (extend && myDraft && myDraft.line != null && myDraft.side === anchor.side) {
      const lo = Math.min(myDraft.line, anchor.line);
      const hi = Math.max(myDraft.endLine ?? myDraft.line, anchor.line);
      setDraft({ ...myDraft, line: lo, endLine: hi === lo ? null : hi });
      return;
    }
    setDraft({ commitKey, path, side: anchor.side, line: anchor.line, endLine: null, viewId });
  };

  const getLineText = (d: NoteDraft) => (d.line == null ? null : linesText(diff, d.side, d.line, d.endLine ?? d.line));

  // Scroll to a jump target (from the notes panel or a deep link).
  useEffect(() => {
    if (stale || !jump || handledJumps.has(jump.nonce) || jump.commitKey !== commitKey || jump.path !== path || jump.line == null || !tableRef.current) return;
    const key = `${jump.side ?? 'New'}:${jump.line}`;
    const row = tableRef.current.querySelector<HTMLElement>(`tr[data-anchor="${key}"]`);
    if (row) {
      handledJumps.add(jump.nonce);
      row.scrollIntoView({ block: 'center' });
      row.classList.remove('flash');
      void row.offsetWidth;
      row.classList.add('flash');
    }
  }, [jump, commitKey, path, stale, diff]);

  if (diff.isBinary) return <div className="diff-message">Binary file — not shown.</div>;
  if (diff.hunks.length === 0) {
    return (
      <div className="diff-message">
        {diff.status === 'Renamed' || diff.status === 'Copied' ? 'File renamed without content changes.' : diff.oldMode !== diff.newMode && diff.oldMode && diff.newMode ? `Mode changed ${diff.oldMode} → ${diff.newMode}.` : 'No content changes.'}
        <FileLevelNotes notes={notes} viewId={viewId} commitKey={commitKey} path={path} />
      </div>
    );
  }

  const threadFor = (key: string | null): ReactNode => {
    if (!key) return null;
    const list = byAnchor.get(key) ?? [];
    const showDraft = draftKey === key;
    if (list.length === 0 && !showDraft) return null;
    return <NoteThread notes={list} draft={showDraft ? myDraft : null} getLineText={getLineText} />;
  };

  const cols = split ? 4 : plain ? 2 : 3;
  const rows: ReactNode[] = [];
  diff.hunks.forEach((hunk, hi) => {
    const hidden = plain ? 0 : hiddenLinesBefore(diff.hunks, hi);
    if (!plain) {
      rows.push(
        <tr key={`h${hi}`} className="hunk-row">
          <td colSpan={cols}>
            {hidden > 0 && !isFull && onExpand ? (
              <button className="expand" onClick={onExpand} title="Show the full file">
                ⋯ {hidden} unchanged line{hidden === 1 ? '' : 's'}
              </button>
            ) : null}
            <span className="hunk-header mono">
              @@ -{hunk.oldStart},{hunk.oldCount} +{hunk.newStart},{hunk.newCount} @@ {hunk.header}
            </span>
          </td>
        </tr>,
      );
    }
    if (split) renderSplit(hunk, hi);
    else renderUnified(hunk, hi);
  });

  function renderUnified(hunk: DiffHunk, hi: number) {
    hunk.lines.forEach((line, li) => {
      const a = anchorFor(line);
      const key = anchorKey(a);
      const cls = `line ${line.type.toLowerCase()} ${inDraft(a) ? 'selected' : ''}`;
      rows.push(
        <tr key={`${hi}-${li}`} className={cls} data-anchor={key}>
          {!plain && <td className="ln">{line.oldLine ?? ''}</td>}
          <td className="ln">{line.newLine ?? ''}</td>
          <CodeCell line={line} lang={lang} anchor={a} onAdd={onGutterClick} marker={!plain} />
        </tr>,
      );
      const thread = threadFor(key);
      if (thread) {
        rows.push(
          <tr key={`${hi}-${li}-n`} className="note-row">
            <td colSpan={cols}>{thread}</td>
          </tr>,
        );
      }
    });
  }

  function renderSplit(hunk: DiffHunk, hi: number) {
    toSplitRows(hunk.lines).forEach((row, ri) => {
      const isContext = row.left?.type === 'Context';
      const la: LineAnchor | null = row.left ? (isContext ? anchorFor(row.left) : { side: 'Old', line: row.left.oldLine! }) : null;
      const ra: LineAnchor | null = row.right ? anchorFor(row.right) : null;
      const anchorAttr = ra ? anchorKey(ra) : la ? anchorKey(la) : undefined;
      rows.push(
        <tr key={`${hi}-${ri}`} className={`line split ${isContext ? 'context' : ''}`} data-anchor={anchorAttr}>
          <td className={`ln ${row.left ? (isContext ? '' : 'delete') : 'empty'}`}>{row.left?.oldLine ?? ''}</td>
          {row.left ? (
            <CodeCell
              line={row.left}
              lang={lang}
              anchor={la!}
              onAdd={onGutterClick}
              marker
              extraClass={`${isContext ? 'context' : 'delete'} ${inDraft(la) ? 'selected' : ''}`}
            />
          ) : (
            <td className="code empty" />
          )}
          <td className={`ln ${row.right ? (isContext ? '' : 'add') : 'empty'}`}>{row.right?.newLine ?? ''}</td>
          {row.right ? (
            <CodeCell
              line={row.right}
              lang={lang}
              anchor={ra!}
              onAdd={onGutterClick}
              marker
              extraClass={`${isContext ? 'context' : 'add'} ${inDraft(ra) ? 'selected' : ''}`}
            />
          ) : (
            <td className="code empty" />
          )}
        </tr>,
      );
      const leftThread = !isContext && la ? threadFor(anchorKey(la)) : null;
      const rightThread = ra ? threadFor(anchorKey(ra)) : null;
      if (leftThread || rightThread) {
        rows.push(
          <tr key={`${hi}-${ri}-n`} className="note-row">
            <td colSpan={2}>{leftThread}</td>
            <td colSpan={2}>{rightThread}</td>
          </tr>,
        );
      }
    });
  }

  const last = diff.hunks[diff.hunks.length - 1];
  return (
    <div className={`diff ${ui.wrap ? 'wrap' : ''}`}>
      <FileLevelNotes notes={unplaced} viewId={viewId} commitKey={commitKey} path={path} />
      <div className="diff-scroll">
        <table ref={tableRef} className={`diff-table ${split ? 'split' : 'unified'} ${plain ? 'plain' : ''}`}>
          <colgroup>
            {split ? (
              <>
                <col className="ln-col" />
                <col />
                <col className="ln-col" />
                <col />
              </>
            ) : (
              <>
                {!plain && <col className="ln-col" />}
                <col className="ln-col" />
                <col />
              </>
            )}
          </colgroup>
          <tbody>
            {rows.map((r, i) => (
              <Fragment key={i}>{r}</Fragment>
            ))}
            {!plain && !isFull && onExpand && last && diff.status !== 'Deleted' && diff.status !== 'Added' && (
              <tr className="hunk-row">
                <td colSpan={cols}>
                  <button className="expand" onClick={onExpand}>
                    ⋯ Expand to full file
                  </button>
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
      {diff.truncated && <div className="diff-message">This diff is very large and was truncated.</div>}
    </div>
  );
}

function CodeCell({
  line,
  lang,
  anchor,
  onAdd,
  marker,
  extraClass = '',
}: {
  line: DiffLine;
  lang: string | null;
  anchor: LineAnchor;
  onAdd: (a: LineAnchor, extend: boolean) => void;
  marker: boolean;
  extraClass?: string;
}) {
  const html = useMemo(() => highlightLine(line.text, lang), [line.text, lang]);
  const sign = line.type === 'Add' ? '+' : line.type === 'Delete' ? '-' : ' ';
  return (
    <td className={`code ${extraClass}`}>
      <button
        className="add-note"
        title="Add a note (shift-click to extend a selection)"
        aria-label={`Add note on line ${anchor.line}`}
        onClick={(e) => onAdd(anchor, e.shiftKey)}
      >
        +
      </button>
      {marker && <span className="marker">{sign}</span>}
      <span className="src" dangerouslySetInnerHTML={{ __html: html || ' ' }} />
      {line.noNewlineAtEnd && <span className="no-eol" title="No newline at end of file">⏎̸</span>}
    </td>
  );
}

/** Notes attached to the whole file, or to lines not visible in the current diff. */
function FileLevelNotes({ notes, viewId, commitKey, path }: { notes: ReviewNote[]; viewId: string; commitKey: string; path: string }) {
  const { draft } = useSession();
  const fileNotes = notes.filter((n) => n.line == null);
  const lineNotes = notes.filter((n) => n.line != null);
  const showDraft = draft && draft.viewId === viewId && draft.commitKey === commitKey && draft.path === path && draft.line == null;
  if (fileNotes.length === 0 && lineNotes.length === 0 && !showDraft) return null;
  return (
    <div className="file-notes">
      <NoteThread notes={fileNotes} draft={showDraft ? draft : null} />
      {lineNotes.length > 0 && (
        <div className="unplaced">
          <div className="muted small">Notes on lines outside the visible diff:</div>
          {lineNotes.map((n) => (
            <div key={n.id}>
              {n.lineText && <pre className="snapshot">{n.lineText}</pre>}
              <NoteCard note={n} />
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

/** Builds a synthetic diff so a whole file (no changes) can be rendered with the same component. */
export function fileAsDiff(path: string, text: string): FileDiff {
  const lines = text.endsWith('\n') ? text.slice(0, -1).split('\n') : text.split('\n');
  return {
    path,
    oldPath: null,
    status: 'Modified',
    isBinary: false,
    oldMode: null,
    newMode: null,
    truncated: false,
    hunks: [
      {
        oldStart: 1,
        oldCount: lines.length,
        newStart: 1,
        newCount: lines.length,
        header: '',
        lines: lines.map((t, i) => ({ type: 'Context', oldLine: null, newLine: i + 1, text: t.replace(/\r$/, ''), noNewlineAtEnd: false })),
      },
    ],
  };
}
