import { Fragment, memo, useEffect, useMemo, useRef, useState, type MouseEvent as ReactMouseEvent, type ReactNode } from 'react';
import { anchorFor, anchorKey, basename, hiddenLinesBefore, linesText, noteAnchorKey, toSplitRows, type LineAnchor } from '../lib/diff';
import { highlightFile, highlightLine, languageFor, type HighlightedFile } from '../lib/highlight';
import { hunkRanges, isHunkReviewed, parseRanges } from '../lib/ranges';
import { notesFor, useSession, type JumpOrigin, type NoteDraft } from '../lib/session';
import { isSpecialKey, type DiffHunk, type DiffLine, type DiffSide, type FileDiff, type LaterChange, type MovedBlock, type NoteFollowUp, type ReviewNote } from '../lib/types';
import { markRanges, wordDiff, type Range } from '../lib/worddiff';
import { KindBadge, NoteCard, NoteThread } from './Notes';
import { LaterChangePeek } from './Peek';

/** Jumps already scrolled to, so remounting a view doesn't scroll again. */
const handledJumps = new Set<number>();

/** Above this many rendered lines, the rest of a diff is behind a "show more" button. */
const RENDER_LIMIT = 2000;

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
  /** Embedded preview (e.g. a peek): no hunk review controls, expanders or file-level notes. */
  compact?: boolean;
  /** A line to highlight, e.g. the one a peek is about. */
  focus?: { side: DiffSide; line: number };
}

interface MovedInfo {
  block: MovedBlock;
  first: boolean;
  /** 'out' = this deleted line was moved elsewhere; 'in' = this added line was moved here. */
  dir: 'out' | 'in';
}

interface Decorations {
  ranges?: Range[];
  moved?: MovedInfo;
  later?: LaterChange;
}

export function DiffView({ viewId, commitKey, path, diff, isFull, onExpand, plain, stale, compact, focus }: Props) {
  const session = useSession();
  const { review, ui, draft, setDraft, jump, jumpTo } = session;
  const lang = useMemo(() => languageFor(path), [path]);
  const tableRef = useRef<HTMLTableElement>(null);
  const split = ui.diffStyle === 'split' && !plain;
  const [limit, setLimit] = useState(RENDER_LIMIT);
  const [openHunks, setOpenHunks] = useState<Set<number>>(new Set());
  const isCommit = !isSpecialKey(commitKey);

  const notes = useMemo(() => notesFor(review, commitKey, path), [review, commitKey, path]);
  const commitNumber = useMemo(() => {
    const m = new Map<string, number>();
    review.commits.filter((c) => !c.outdated).forEach((c, i) => m.set(c.sha, i + 1));
    return m;
  }, [review.commits]);

  // ---- Syntax highlighting of whole file versions, so multi-line comments/strings colour correctly ----
  const highlighted = useWholeFileHighlighting(commitKey, path, diff, lang);

  // ---- Moved code (computed per commit across all files) ----
  const { loadMoved } = session;
  useEffect(() => {
    if (!plain) loadMoved(commitKey);
  }, [plain, commitKey, loadMoved]);
  const movedBlocks = session.moved(commitKey);
  const moved = useMemo(() => {
    const out = new Map<number, MovedInfo>();
    const inn = new Map<number, MovedInfo>();
    const oldPath = diff.oldPath ?? path;
    for (const block of movedBlocks ?? []) {
      if (block.fromPath === oldPath)
        for (let l = block.fromStart; l <= block.fromEnd; l++) out.set(l, { block, first: l === block.fromStart, dir: 'out' });
      if (block.toPath === path) for (let l = block.toStart; l <= block.toEnd; l++) inn.set(l, { block, first: l === block.toStart, dir: 'in' });
    }
    return { out, inn };
  }, [movedBlocks, diff.oldPath, path]);

  // ---- Lines a later commit rewrites ----
  const [later, setLater] = useState<Map<number, LaterChange>>(new Map());
  const { getLaterChanges } = session;
  useEffect(() => {
    if (plain || !isCommit || diff.status === 'Deleted') {
      setLater(new Map());
      return;
    }
    let cancelled = false;
    getLaterChanges(commitKey, path).then((list) => !cancelled && setLater(new Map(list.map((c) => [c.line, c]))));
    return () => {
      cancelled = true;
    };
  }, [plain, isCommit, commitKey, path, diff.status, getLaterChanges]);

  // ---- Notes carried over from earlier commits whose code this commit changes ----
  const carried = useMemo(() => {
    const map = new Map<string, { followUp: NoteFollowUp; note: ReviewNote }[]>();
    for (const f of session.followUps) {
      if (f.commitSha !== commitKey || f.path !== path) continue;
      const note = review.notes.find((n) => n.id === f.noteId);
      if (!note) continue;
      const key = `Old:${f.endLine}`;
      map.set(key, [...(map.get(key) ?? []), { followUp: f, note }]);
    }
    return map;
  }, [session.followUps, review.notes, commitKey, path]);

  // ---- Per-line decorations: intra-line word changes, moves, later rewrites ----
  const decorations = useMemo(() => {
    const deco = new Map<DiffLine, Decorations>();
    const get = (l: DiffLine) => deco.get(l) ?? (deco.set(l, {}), deco.get(l)!);
    for (const h of diff.hunks) {
      for (const l of h.lines) {
        if (l.type === 'Delete' && moved.out.has(l.oldLine!)) get(l).moved = moved.out.get(l.oldLine!);
        if (l.type === 'Add') {
          if (moved.inn.has(l.newLine!)) get(l).moved = moved.inn.get(l.newLine!);
          const lc = later.get(l.newLine!);
          if (lc) get(l).later = lc;
        }
      }
      if (plain) continue;
      for (const row of toSplitRows(h.lines)) {
        if (!row.left || !row.right || row.left.type !== 'Delete') continue;
        if (deco.get(row.left)?.moved || deco.get(row.right)?.moved) continue;
        const wd = wordDiff(row.left.text, row.right.text);
        if (!wd) continue;
        get(row.left).ranges = wd.old;
        get(row.right).ranges = wd.new;
      }
    }
    return deco;
  }, [diff, moved, later, plain]);

  const laterAdded = useMemo(() => {
    const set = new Set<number>();
    for (const h of diff.hunks) for (const l of h.lines) if (l.type === 'Add' && later.has(l.newLine!)) set.add(l.newLine!);
    return set;
  }, [diff, later]);

  // Index notes by the line they render under; anything we can't place goes in the header.
  const { byAnchor, unplaced, unplacedCarried } = useMemo(() => {
    const present = new Set<string>();
    for (const h of diff.hunks) for (const l of h.lines) present.add(anchorKey(anchorFor(l)));
    const map = new Map<string, ReviewNote[]>();
    const rest: ReviewNote[] = [];
    for (const n of notes) {
      const key = noteAnchorKey(n);
      if (key && present.has(key)) map.set(key, [...(map.get(key) ?? []), n]);
      else rest.push(n);
    }
    const restCarried = [...carried.entries()].filter(([k]) => !present.has(k)).flatMap(([, v]) => v);
    return { byAnchor: map, unplaced: rest, unplacedCarried: restCarried };
  }, [diff, notes, carried]);

  // ---- Hunk-level review progress ----
  const fileReviewed = session.isReviewed(commitKey, path);
  const lineRanges = session.reviewedLines(commitKey, path);
  const reviewedSet = useMemo(() => parseRanges(lineRanges), [lineRanges]);
  const hunkDone = (h: DiffHunk) => fileReviewed || isHunkReviewed(h, reviewedSet);
  const reviewable = !plain && diff.hunks.some((h) => h.lines.some((l) => l.type !== 'Context'));

  const toggleHunk = async (index: number) => {
    const hunk = diff.hunks[index];
    if (fileReviewed) {
      // Un-reviewing one hunk of a reviewed file: keep the others reviewed.
      await session.setReviewed(commitKey, [path], false);
      const others = diff.hunks.filter((_, i) => i !== index).flatMap(hunkRanges);
      if (others.length) await session.setLinesReviewed(commitKey, path, others, true);
      return;
    }
    const nowReviewed = !isHunkReviewed(hunk, reviewedSet);
    await session.setLinesReviewed(commitKey, path, hunkRanges(hunk), nowReviewed);
    if (nowReviewed && diff.hunks.every((h, i) => i === index || isHunkReviewed(h, reviewedSet) || !h.lines.some((l) => l.type !== 'Context')))
      await session.setReviewed(commitKey, [path], true);
    setOpenHunks((s) => {
      const next = new Set(s);
      next.delete(index);
      return next;
    });
  };

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

  // Scroll to a jump target (from the notes panel, a moved-code link, a later-change marker or "Back").
  // The highlight lives in state (not a DOM class) so re-renders while data loads don't wipe it.
  const [flash, setFlash] = useState<string | null>(null);
  useEffect(() => {
    if (stale || !jump || handledJumps.has(jump.nonce) || jump.commitKey !== commitKey || jump.path !== path || jump.line == null || !tableRef.current) return;
    const key = `${jump.side ?? 'New'}:${jump.line}`;
    const row = tableRef.current.querySelector<HTMLElement>(`tr[data-anchor="${key}"], tr[data-anchor-old="${key}"]`);
    if (!row) return;
    handledJumps.add(jump.nonce);
    row.scrollIntoView({ block: 'center' });
    setFlash(key);
    const t = window.setTimeout(() => setFlash(null), 1800);
    return () => window.clearTimeout(t);
  }, [jump, commitKey, path, stale, diff]);

  if (diff.isBinary) return <div className="diff-message">Binary file — not shown.</div>;
  if (diff.hunks.length === 0) {
    return (
      <div className="diff-message">
        {ui.ignoreWhitespace
          ? 'Only whitespace changes (hidden).'
          : diff.status === 'Renamed' || diff.status === 'Copied'
            ? 'File renamed without content changes.'
            : diff.oldMode !== diff.newMode && diff.oldMode && diff.newMode
              ? `Mode changed ${diff.oldMode} → ${diff.newMode}.`
              : 'No content changes.'}
        <FileLevelNotes notes={notes} carried={[]} viewId={viewId} commitKey={commitKey} path={path} />
      </div>
    );
  }

  const threadFor = (key: string | null): ReactNode => {
    if (!key) return null;
    const list = byAnchor.get(key) ?? [];
    const carriedHere = carried.get(key) ?? [];
    const showDraft = draftKey === key;
    if (list.length === 0 && carriedHere.length === 0 && !showDraft) return null;
    return (
      <>
        {carriedHere.map((c) => (
          <CarriedNote key={c.note.id} note={c.note} followUp={c.followUp} />
        ))}
        {(list.length > 0 || showDraft) && <NoteThread notes={list} draft={showDraft ? myDraft : null} getLineText={getLineText} />}
      </>
    );
  };

  // Links remember where they were clicked, so the reviewer can come back ("← Back").
  const here = (line: DiffLine): JumpOrigin => {
    const a = anchorFor(line);
    return { commitKey, path, side: a.side, line: a.line };
  };
  const goMoved = (m: MovedInfo, line: DiffLine) =>
    m.dir === 'out'
      ? jumpTo({ commitKey, path: m.block.toPath, side: 'New', line: m.block.toStart }, here(line))
      : jumpTo({ commitKey, path: m.block.fromPath, side: 'Old', line: m.block.fromStart }, here(line));
  const goLater = (c: LaterChange, line: DiffLine) => jumpTo({ commitKey: c.commitSha, path, side: 'Old', line: c.oldLine }, here(line));

  // ---- Peeking at a later change in place ----
  const [peek, setPeek] = useState<{ line: number; change: LaterChange } | null>(null);
  const onLaterClick = (e: ReactMouseEvent, c: LaterChange, line: DiffLine) => {
    if (e.ctrlKey || e.metaKey) return goLater(c, line);
    setPeek((p) => (p?.line === line.newLine ? null : { line: line.newLine!, change: c }));
  };
  const peekRow = (line: DiffLine | null | undefined, key: string): ReactNode =>
    peek && line?.type === 'Add' && line.newLine === peek.line ? (
      <tr key={key} className="peek-row">
        <td colSpan={cols}>
          <LaterChangePeek change={peek.change} fromCommit={commitKey} path={path} line={peek.line} viewId={viewId} onClose={() => setPeek(null)} />
        </td>
      </tr>
    ) : null;
  const isFocus = (line: DiffLine | null | undefined) =>
    !!(focus && line && (focus.side === 'Old' ? line.type !== 'Add' && line.oldLine === focus.line : line.type !== 'Delete' && line.newLine === focus.line));

  const badges = (line: DiffLine): ReactNode => {
    const d = decorations.get(line);
    if (!d) return null;
    const out: ReactNode[] = [];
    if (d.moved?.first) {
      const b = d.moved.block;
      const target = d.moved.dir === 'out' ? `${b.toPath === path ? '' : basename(b.toPath)}:${b.toStart}` : `${b.fromPath === (diff.oldPath ?? path) ? '' : basename(b.fromPath)}:${b.fromStart}`;
      out.push(
        <button key="m" className="chip moved-chip" onClick={() => goMoved(d.moved!, line)} title={`${b.toEnd - b.toStart + 1} lines moved ${d.moved.dir === 'out' ? 'to' : 'from'} ${d.moved.dir === 'out' ? b.toPath : b.fromPath}`}>
          {d.moved.dir === 'out' ? '⇢ moved to' : '⇠ moved from'} {target.startsWith(':') ? `line ${target.slice(1)}` : target}
        </button>,
      );
    }
    return out.length ? out : null;
  };

  // ---- "Changes again later" gutter, left of the line numbers so it's always in the same place ----
  // Shown in commits that have later commits (known up front, so the layout doesn't shift when data loads).
  const activeCommits = review.commits.filter((c) => !c.outdated);
  const showLaterGutter = !plain && isCommit && activeCommits.findIndex((c) => c.sha === commitKey) < activeCommits.length - 1;
  const laterCell = (line: DiffLine | null | undefined, key?: string): ReactNode => {
    if (!showLaterGutter) return null;
    const lc = line?.type === 'Add' ? decorations.get(line)?.later : undefined;
    if (!line || !lc) return <td key={key} className="later-cell" />;
    // One button per run of consecutive lines rewritten by the same later commit; the rest get a stripe.
    const prev = later.get(line.newLine! - 1);
    const continues = !!prev && prev.commitSha === lc.commitSha && laterAdded.has(line.newLine! - 1);
    const n = commitNumber.get(lc.commitSha);
    const subject = review.commits.find((c) => c.sha === lc.commitSha)?.subject ?? '';
    return (
      <td key={key} className={`later-cell has ${continues ? 'run' : 'start'}`}>
        {!continues && (
          <button
            className={`later-chip ${peek?.line === line.newLine ? 'active' : ''}`}
            onClick={(e) => onLaterClick(e, lc, line)}
            title={`Changed again in commit ${n}: ${subject}\nClick to peek here · Ctrl+click to go to commit ${n}`}
            aria-label={`Changes again in commit ${n}`}
          >
            ↻{n}
          </button>
        )}
      </td>
    );
  };

  const lineClass = (line: DiffLine) => {
    const d = decorations.get(line);
    return `${d?.moved ? 'moved' : ''} ${d?.later && ui.showLater ? 'superseded' : ''}`;
  };

  const cols = (split ? 4 : plain ? 2 : 3) + (showLaterGutter ? 1 : 0);
  const rows: ReactNode[] = [];
  let rendered = 0;
  let remaining = 0;
  diff.hunks.forEach((hunk, hi) => {
    if (rendered >= limit) {
      remaining += hunk.lines.length;
      return;
    }
    const hidden = plain ? 0 : hiddenLinesBefore(diff.hunks, hi);
    const done = reviewable && hunkDone(hunk);
    const collapsed = done && !openHunks.has(hi);
    const changes = hunk.lines.filter((l) => l.type !== 'Context').length;
    if (!plain) {
      rows.push(
        <tr key={`h${hi}`} className={`hunk-row ${done ? 'hunk-done' : ''}`}>
          <td colSpan={cols}>
            <div className="hunk-bar">
              {hidden > 0 && !isFull && onExpand && !compact ? (
                <button className="expand" onClick={onExpand} title="Show the full file">
                  ⋯ {hidden} unchanged line{hidden === 1 ? '' : 's'}
                </button>
              ) : null}
              <span className="hunk-header mono">
                @@ -{hunk.oldStart},{hunk.oldCount} +{hunk.newStart},{hunk.newCount} @@ {hunk.header}
              </span>
              <span className="spacer" />
              {collapsed && (
                <button className="expand" onClick={() => setOpenHunks((s) => new Set(s).add(hi))}>
                  show {changes} reviewed change{changes === 1 ? '' : 's'}
                </button>
              )}
              {done && !collapsed && (
                <button
                  className="expand"
                  onClick={() =>
                    setOpenHunks((s) => {
                      const n = new Set(s);
                      n.delete(hi);
                      return n;
                    })
                  }
                >
                  hide
                </button>
              )}
              {reviewable && changes > 0 && !compact && (
                <label className="hunk-check" title="Mark just this part of the file as reviewed">
                  <input type="checkbox" checked={done} onChange={() => toggleHunk(hi)} /> {done ? 'Reviewed' : 'Hunk reviewed'}
                </label>
              )}
            </div>
          </td>
        </tr>,
      );
    }
    if (collapsed) return;
    if (split) renderSplit(hunk, hi);
    else renderUnified(hunk, hi);
  });

  function renderUnified(hunk: DiffHunk, hi: number) {
    hunk.lines.forEach((line, li) => {
      if (rendered >= limit) {
        remaining++;
        return;
      }
      rendered++;
      const a = anchorFor(line);
      const key = anchorKey(a);
      const cls = `line ${line.type.toLowerCase()} ${inDraft(a) ? 'selected' : ''} ${lineClass(line)} ${isFocus(line) ? 'peek-target' : ''} ${flash === key ? 'flash' : ''}`;
      rows.push(
        <tr key={`${hi}-${li}`} className={cls} data-anchor={key}>
          {laterCell(line)}
          {!plain && <td className="ln">{line.oldLine ?? ''}</td>}
          <td className="ln">{line.newLine ?? ''}</td>
          <CodeCell line={line} lang={lang} baseHtml={highlighted(line)} anchor={a} onAdd={onGutterClick} marker={!plain} ranges={decorations.get(line)?.ranges} badges={badges(line)} />
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
      const p = peekRow(line, `${hi}-${li}-p`);
      if (p) rows.push(p);
    });
  }

  function renderSplit(hunk: DiffHunk, hi: number) {
    toSplitRows(hunk.lines).forEach((row, ri) => {
      if (rendered >= limit) {
        remaining++;
        return;
      }
      rendered++;
      const isContext = row.left?.type === 'Context';
      const la: LineAnchor | null = row.left ? (isContext ? anchorFor(row.left) : { side: 'Old', line: row.left.oldLine! }) : null;
      const ra: LineAnchor | null = row.right ? anchorFor(row.right) : null;
      // Deleted lines are addressable by their old-side anchor too (for jumps to "Old:n").
      const anchorAttr = !isContext && la && !ra ? anchorKey(la) : ra ? anchorKey(ra) : la ? anchorKey(la) : undefined;
      rows.push(
        <tr
          key={`${hi}-${ri}`}
          className={`line split ${isContext ? 'context' : ''} ${isFocus(row.left) || isFocus(row.right) ? 'peek-target' : ''} ${
            flash && (flash === anchorAttr || (la && !isContext && flash === anchorKey(la))) ? 'flash' : ''
          }`}
          data-anchor={anchorAttr} data-anchor-old={la && !isContext ? anchorKey(la) : undefined}>
          <td className={`ln ${row.left ? (isContext ? '' : 'delete') : 'empty'}`}>{row.left?.oldLine ?? ''}</td>
          {row.left ? (
            <CodeCell
              line={row.left}
              lang={lang}
              baseHtml={highlighted(row.left)}
              anchor={la!}
              onAdd={onGutterClick}
              marker
              ranges={decorations.get(row.left)?.ranges}
              badges={isContext ? null : badges(row.left)}
              extraClass={`${isContext ? 'context' : 'delete'} ${inDraft(la) ? 'selected' : ''} ${lineClass(row.left)}`}
            />
          ) : (
            <td className="code empty" />
          )}
          {laterCell(row.right)}
          <td className={`ln ${row.right ? (isContext ? '' : 'add') : 'empty'}`}>{row.right?.newLine ?? ''}</td>
          {row.right ? (
            <CodeCell
              line={row.right}
              lang={lang}
              baseHtml={highlighted(row.right)}
              anchor={ra!}
              onAdd={onGutterClick}
              marker
              ranges={decorations.get(row.right)?.ranges}
              badges={badges(row.right)}
              extraClass={`${isContext ? 'context' : 'add'} ${inDraft(ra) ? 'selected' : ''} ${lineClass(row.right)}`}
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
            <td colSpan={showLaterGutter ? 3 : 2}>{rightThread}</td>
          </tr>,
        );
      }
      const p = peekRow(row.right, `${hi}-${ri}-p`);
      if (p) rows.push(p);
    });
  }

  const last = diff.hunks[diff.hunks.length - 1];
  return (
    <div className={`diff ${ui.wrap ? 'wrap' : ''}`}>
      {!compact && <FileLevelNotes notes={unplaced} carried={unplacedCarried} viewId={viewId} commitKey={commitKey} path={path} />}
      <div className="diff-scroll">
        <table ref={tableRef} className={`diff-table ${split ? 'split' : 'unified'} ${plain ? 'plain' : ''}`}>
          <colgroup>
            {split ? (
              <>
                <col className="ln-col" />
                <col />
                {showLaterGutter && <col className="later-col" />}
                <col className="ln-col" />
                <col />
              </>
            ) : (
              <>
                {showLaterGutter && <col className="later-col" />}
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
            {remaining > 0 && (
              <tr className="hunk-row">
                <td colSpan={cols}>
                  <button className="expand" onClick={() => setLimit((l) => l + RENDER_LIMIT * 5)}>
                    ⋯ {remaining.toLocaleString()} more lines not rendered yet — show more
                  </button>
                </td>
              </tr>
            )}
            {remaining === 0 && !plain && !compact && !isFull && onExpand && last && diff.status !== 'Deleted' && diff.status !== 'Added' && (
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

const CodeCell = memo(function CodeCell({
  line,
  lang,
  baseHtml,
  anchor,
  onAdd,
  marker,
  ranges,
  badges,
  extraClass = '',
}: {
  line: DiffLine;
  lang: string | null;
  /** Line HTML from whole-file highlighting; falls back to highlighting the line on its own. */
  baseHtml?: string;
  anchor: LineAnchor;
  onAdd: (a: LineAnchor, extend: boolean) => void;
  marker: boolean;
  ranges?: Range[];
  badges?: ReactNode;
  extraClass?: string;
}) {
  const html = useMemo(() => {
    const base = baseHtml ?? highlightLine(line.text, lang);
    return ranges && ranges.length ? markRanges(base, ranges, line.type === 'Delete' ? 'wd wd-del' : 'wd wd-add') : base;
  }, [line.text, line.type, lang, ranges, baseHtml]);
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
      {badges && <span className="badges">{badges}</span>}
    </td>
  );
});

/**
 * Highlights the old and new versions of the file as wholes (fetched once and cached), and returns a
 * lookup from a diff line to its highlighted HTML. Lines are matched by number and verified by text;
 * anything that doesn't match (or a file too large to highlight whole) uses per-line highlighting.
 */
function useWholeFileHighlighting(commitKey: string, path: string, diff: FileDiff, lang: string | null) {
  const { getBlob } = useSession();
  const [files, setFiles] = useState<{ key: string; old: HighlightedFile | null; new: HighlightedFile | null } | null>(null);
  const oldPath = diff.oldPath ?? path;
  const needOld = diff.status !== 'Added' && diff.hunks.some((h) => h.lines.some((l) => l.type === 'Delete'));
  const needNew = diff.status !== 'Deleted' && diff.hunks.some((h) => h.lines.some((l) => l.type !== 'Delete'));
  const key = `${commitKey}|${oldPath}|${path}|${needOld}|${needNew}`;

  useEffect(() => {
    if (!lang || diff.isBinary || (!needOld && !needNew)) return;
    let cancelled = false;
    const load = (p: string, side: 'Old' | 'New', needed: boolean) =>
      needed
        ? getBlob(commitKey, p, side).then(
            (b) => (b.text != null ? highlightFile(b.text, lang, `${commitKey}|${side}|${p}|${b.sha}`) : null),
            () => null,
          )
        : Promise.resolve(null);
    Promise.all([load(oldPath, 'Old', needOld), load(path, 'New', needNew)]).then(([o, n]) => {
      if (!cancelled) setFiles({ key, old: o, new: n });
    });
    return () => {
      cancelled = true;
    };
  }, [key, lang, diff.isBinary, getBlob, commitKey, oldPath, path, needOld, needNew]);

  const current = files?.key === key ? files : null;
  return (line: DiffLine): string | undefined => {
    if (!current) return undefined;
    const file = line.type === 'Delete' ? current.old : current.new;
    const n = line.type === 'Delete' ? line.oldLine : line.newLine;
    if (!file || n == null || file.lines[n - 1] !== line.text) return undefined;
    return file.html[n - 1];
  };
}

/** A note from an earlier commit, shown where a later commit changes the code it was about. */
function CarriedNote({ note, followUp }: { note: ReviewNote; followUp: NoteFollowUp }) {
  const { review, updateNote, jumpTo } = useSession();
  const index = review.commits.filter((c) => !c.outdated).findIndex((c) => c.sha === note.commitSha);
  const partial = followUp.changedLines < followUp.totalLines;
  return (
    <div className="carried-note">
      <div className="note-head">
        <span className="carried-label">↻ From commit {index + 1}</span>
        <KindBadge kind={note.kind} />
        <span className="muted small">
          {partial ? `${followUp.changedLines} of ${followUp.totalLines} lines it covers change here` : 'the code it refers to changes here'} — addressed?
        </span>
        <span className="spacer" />
        <button className="btn-link" onClick={() => updateNote(note.id, { resolved: true })}>
          Resolve
        </button>
        <button
          className="btn-link"
          onClick={() =>
            jumpTo(
              { commitKey: note.commitSha, path: note.filePath ?? null, side: note.side, line: note.endLine ?? note.line },
              { commitKey: followUp.commitSha, path: followUp.path, side: 'Old', line: followUp.endLine },
            )
          }
        >
          View original
        </button>
      </div>
      <div className="note-body">{note.body}</div>
    </div>
  );
}

/** Notes attached to the whole file, or to lines not visible in the current diff. */
function FileLevelNotes({
  notes,
  carried,
  viewId,
  commitKey,
  path,
}: {
  notes: ReviewNote[];
  carried: { followUp: NoteFollowUp; note: ReviewNote }[];
  viewId: string;
  commitKey: string;
  path: string;
}) {
  const { draft } = useSession();
  const fileNotes = notes.filter((n) => n.line == null);
  const lineNotes = notes.filter((n) => n.line != null);
  const showDraft = draft && draft.viewId === viewId && draft.commitKey === commitKey && draft.path === path && draft.line == null;
  if (fileNotes.length === 0 && lineNotes.length === 0 && carried.length === 0 && !showDraft) return null;
  return (
    <div className="file-notes">
      {carried.map((c) => (
        <CarriedNote key={c.note.id} note={c.note} followUp={c.followUp} />
      ))}
      {(fileNotes.length > 0 || showDraft) && <NoteThread notes={fileNotes} draft={showDraft ? draft : null} />}
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
