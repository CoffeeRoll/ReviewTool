import { useEffect, useRef, useState } from 'react';
import { relativeTime } from '../lib/diff';
import { useSession, type NoteDraft } from '../lib/session';
import { NOTE_KINDS, type NoteKind, type ReviewNote } from '../lib/types';

export function KindBadge({ kind }: { kind: NoteKind }) {
  return <span className={`kind kind-${kind.toLowerCase()}`}>{kind}</span>;
}

/** Renders note text with `inline code` and fenced ``` blocks; everything else is plain text. */
function NoteBody({ text }: { text: string }) {
  const parts = text.split(/(```[\s\S]*?```)/g);
  return (
    <div className="note-body">
      {parts.map((part, i) => {
        if (part.startsWith('```') && part.endsWith('```') && part.length >= 6) {
          const code = part.slice(3, -3).replace(/^[^\n]*\n/, (m) => (m.trim().length < 20 ? '' : m));
          return <pre key={i}>{code}</pre>;
        }
        return (
          <span key={i}>
            {part.split(/(`[^`\n]+`)/g).map((seg, j) =>
              seg.startsWith('`') && seg.endsWith('`') && seg.length > 2 ? <code key={j}>{seg.slice(1, -1)}</code> : seg,
            )}
          </span>
        );
      })}
    </div>
  );
}

export function NoteCard({ note, compact }: { note: ReviewNote; compact?: boolean }) {
  const { updateNote, deleteNote, followUps, review, jumpTo } = useSession();
  const [editing, setEditing] = useState(false);
  const followUp = followUps.find((f) => f.noteId === note.id);

  if (editing) {
    return (
      <NoteEditor
        initialBody={note.body}
        initialKind={note.kind}
        submitLabel="Save"
        onCancel={() => setEditing(false)}
        onSubmit={async (body, kind) => {
          await updateNote(note.id, { body, kind });
          setEditing(false);
        }}
      />
    );
  }

  const range = note.line != null ? `L${note.line}${note.endLine ? `–${note.endLine}` : ''}${note.side === 'Old' ? ' (old)' : ''}` : null;

  return (
    <div className={`note-card ${note.resolved ? 'resolved' : ''}`} data-note-id={note.id}>
      <div className="note-head">
        <KindBadge kind={note.kind} />
        {range && !compact && <span className="muted mono">{range}</span>}
        <span className="muted" title={new Date(note.createdAt).toLocaleString()}>
          {relativeTime(note.updatedAt)}
        </span>
        <span className="spacer" />
        <button className="btn-link" onClick={() => updateNote(note.id, { resolved: !note.resolved })}>
          {note.resolved ? 'Reopen' : 'Resolve'}
        </button>
        <button className="btn-link" onClick={() => setEditing(true)}>
          Edit
        </button>
        <button
          className="btn-link danger"
          onClick={() => {
            if (confirm('Delete this note?')) deleteNote(note.id);
          }}
        >
          Delete
        </button>
      </div>
      {!(compact && note.resolved) && <NoteBody text={note.body} />}
      {followUp && !note.resolved && (
        <button
          className="btn-link small followup-link"
          onClick={() => jumpTo({ commitKey: followUp.commitSha, path: followUp.path, side: 'Old', line: followUp.endLine })}
        >
          ↻ This code changes again in commit {review.commits.filter((c) => !c.outdated).findIndex((c) => c.sha === followUp.commitSha) + 1} — see whether
          it's addressed →
        </button>
      )}
    </div>
  );
}

export function NoteEditor({
  initialBody = '',
  initialKind = 'Comment',
  submitLabel = 'Add note',
  rangeLabel,
  onSubmit,
  onCancel,
}: {
  initialBody?: string;
  initialKind?: NoteKind;
  submitLabel?: string;
  rangeLabel?: string | null;
  onSubmit: (body: string, kind: NoteKind) => Promise<void> | void;
  onCancel: () => void;
}) {
  const [body, setBody] = useState(initialBody);
  const [kind, setKind] = useState<NoteKind>(initialKind);
  const [busy, setBusy] = useState(false);
  const ref = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    ref.current?.focus();
    ref.current?.setSelectionRange(body.length, body.length);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const submit = async () => {
    if (!body.trim() || busy) return;
    setBusy(true);
    try {
      await onSubmit(body, kind);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="note-editor" onKeyDown={(e) => e.stopPropagation()}>
      <textarea
        ref={ref}
        value={body}
        rows={3}
        placeholder="Leave a note… (Ctrl+Enter to save, Esc to cancel)"
        onChange={(e) => setBody(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
            e.preventDefault();
            submit();
          } else if (e.key === 'Escape') {
            e.preventDefault();
            onCancel();
          }
        }}
      />
      <div className="note-editor-actions">
        <div className="kind-picker" role="radiogroup" aria-label="Note type">
          {NOTE_KINDS.map((k) => (
            <button
              key={k}
              type="button"
              role="radio"
              aria-checked={kind === k}
              className={`kind kind-${k.toLowerCase()} ${kind === k ? 'selected' : ''}`}
              onClick={() => setKind(k)}
            >
              {k}
            </button>
          ))}
        </div>
        {rangeLabel && <span className="muted mono">{rangeLabel}</span>}
        <span className="spacer" />
        <button className="btn" onClick={onCancel}>
          Cancel
        </button>
        <button className="btn primary" disabled={!body.trim() || busy} onClick={submit}>
          {submitLabel}
        </button>
      </div>
    </div>
  );
}

/** Existing notes for an anchor plus the draft editor if the draft targets it. */
export function NoteThread({
  notes,
  draft,
  getLineText,
}: {
  notes: ReviewNote[];
  draft?: NoteDraft | null;
  getLineText?: (d: NoteDraft) => string | null;
}) {
  const { addNote, setDraft } = useSession();
  return (
    <div className="note-thread">
      {notes.map((n) => (
        <NoteCard key={n.id} note={n} />
      ))}
      {draft && (
        <NoteEditor
          rangeLabel={
            draft.line != null
              ? `L${draft.line}${draft.endLine && draft.endLine !== draft.line ? `–${draft.endLine}` : ''}${
                  draft.endLine == null || draft.endLine === draft.line ? ' · shift-click + to select a range' : ''
                }`
              : null
          }
          onCancel={() => setDraft(null)}
          onSubmit={async (body, kind) => {
            const note = await addNote({
              commitSha: draft.commitKey,
              filePath: draft.path,
              side: draft.side,
              line: draft.line,
              endLine: draft.endLine,
              lineText: getLineText?.(draft) ?? null,
              body,
              kind,
            });
            if (note) setDraft(null);
          }}
        />
      )}
    </div>
  );
}
