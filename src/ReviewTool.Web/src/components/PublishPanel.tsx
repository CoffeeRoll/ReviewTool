import { useEffect, useMemo, useState } from 'react';
import { api } from '../lib/api';
import { relativeTime } from '../lib/diff';
import { useSession } from '../lib/session';
import type { PublicationRecord, PublishPreview, PublishTarget, PublishVerdict, ReviewStatus } from '../lib/types';
import { KindBadge } from './Notes';

const VERDICTS: { value: PublishVerdict; label: string }[] = [
  { value: 'Comment', label: 'Comment' },
  { value: 'Approve', label: 'Approve' },
  { value: 'RequestChanges', label: 'Request changes' },
];

export function verdictFor(status: ReviewStatus): PublishVerdict {
  return status === 'Approved' ? 'Approve' : status === 'ChangesRequested' ? 'RequestChanges' : 'Comment';
}

/**
 * Posts the review to the platform hosting the repository (GitHub today). The server decides which
 * notes can be inline comments; this panel shows that plan before anything is sent.
 */
export function PublishPanel({ status, summary }: { status: ReviewStatus; summary: string }) {
  const { review } = useSession();
  const [targets, setTargets] = useState<PublishTarget[] | null>(null);
  const [targetIndex, setTargetIndex] = useState(0);
  const [prNumber, setPrNumber] = useState(review.pullRequest?.number ? String(review.pullRequest.number) : '');
  const [verdict, setVerdict] = useState<PublishVerdict>(verdictFor(status));
  const [verdictTouched, setVerdictTouched] = useState(false);
  const [includeResolved, setIncludeResolved] = useState(false);
  const [republish, setRepublish] = useState(false);
  const [preview, setPreview] = useState<PublishPreview | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [posting, setPosting] = useState(false);
  const [postError, setPostError] = useState<string | null>(null);
  const [published, setPublished] = useState<PublicationRecord[]>(review.publications ?? []);
  const [lastResult, setLastResult] = useState<{ url: string; warnings: string[] } | null>(null);
  const [showDetails, setShowDetails] = useState(false);

  useEffect(() => {
    api.publishTargets(review.id).then(setTargets, (e: Error) => {
      setTargets([]);
      setPreviewError(e.message);
    });
  }, [review.id]);

  useEffect(() => {
    if (!verdictTouched) setVerdict(verdictFor(status));
  }, [status, verdictTouched]);

  const target = targets?.[targetIndex];
  const request = useMemo(
    () => ({
      provider: target?.provider,
      remote: target?.remote,
      pullRequestNumber: Number(prNumber) > 0 ? Number(prNumber) : undefined,
      verdict,
      includeResolved,
      republish,
    }),
    [target, prNumber, verdict, includeResolved, republish],
  );

  // Refresh the preview when anything that affects it changes (including the summary, saved by the parent).
  useEffect(() => {
    if (!target) return;
    const ctrl = new AbortController();
    const t = window.setTimeout(() => {
      setLoading(true);
      api.publishPreview(review.id, request, ctrl.signal).then(
        (p) => {
          setPreview(p);
          setPreviewError(null);
          setLoading(false);
          if (!prNumber && p.pullRequest) setPrNumber(String(p.pullRequest.number));
        },
        (e: Error) => {
          if (e.name === 'AbortError') return;
          setPreviewError(e.message);
          setLoading(false);
        },
      );
    }, 600);
    return () => {
      window.clearTimeout(t);
      ctrl.abort();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [review.id, request, summary, published.length]);

  if (!targets) return <p className="muted">Looking for a GitHub remote…</p>;
  if (targets.length === 0)
    return (
      <div className="publish-empty">
        <p>
          None of this repository's remotes is on GitHub, so there's nowhere to post this review yet.
          {previewError && <span className="error"> {previewError}</span>}
        </p>
        <p className="muted small">Use the Markdown export instead, or add a GitHub remote (e.g. <code>git remote add origin git@github.com:owner/repo.git</code>).</p>
      </div>
    );

  const post = async () => {
    if (!preview?.pullRequest) return;
    const what = VERDICTS.find((v) => v.value === verdict)!.label.toLowerCase();
    if (!confirm(`Post this review to ${preview.target.repository}#${preview.pullRequest.number} as "${what}"? This is visible to everyone on the pull request.`)) return;
    setPosting(true);
    setPostError(null);
    try {
      const result = await api.publish(review.id, request);
      setPublished((p) => [...p, result.record]);
      setLastResult({ url: result.url, warnings: result.warnings });
    } catch (e) {
      setPostError((e as Error).message);
    } finally {
      setPosting(false);
    }
  };

  const nothingToPost = !!preview?.nothingNew;

  return (
    <div className="publish">
      <div className="publish-target">
        {targets.length > 1 ? (
          <select className="input small" value={targetIndex} onChange={(e) => setTargetIndex(Number(e.target.value))}>
            {targets.map((t, i) => (
              <option key={`${t.remote}-${t.repository}`} value={i}>
                {t.repository} ({t.remote})
              </option>
            ))}
          </select>
        ) : (
          <a href={target!.webUrl} target="_blank" rel="noreferrer" className="mono">
            {target!.repository}
          </a>
        )}
        <span className="muted small">
          on {target!.providerName} · {target!.authenticated ? `signed in via ${target!.authSource}` : 'not signed in'}
        </span>
      </div>

      <div className="form-grid publish-form">
        <label>
          Pull request
          <div className="row">
            <span className="hash">#</span>
            <input className="input" inputMode="numeric" value={prNumber} placeholder="auto-detect from branch" onChange={(e) => setPrNumber(e.target.value.replace(/\D/g, ''))} />
          </div>
        </label>
        <label>
          Post as
          <div className="seg verdict-seg">
            {VERDICTS.map((v) => (
              <button
                key={v.value}
                type="button"
                className={verdict === v.value ? 'on' : ''}
                onClick={() => {
                  setVerdict(v.value);
                  setVerdictTouched(true);
                }}
              >
                {v.label}
              </button>
            ))}
          </div>
        </label>
      </div>

      {preview?.pullRequest && (
        <div className="pr-card">
          <a href={preview.pullRequest.url} target="_blank" rel="noreferrer">
            <strong>#{preview.pullRequest.number}</strong> {preview.pullRequest.title}
          </a>
          <span className="muted small">
            {preview.pullRequest.headRef} → {preview.pullRequest.baseRef} · {preview.pullRequest.state}
          </span>
        </div>
      )}

      {previewError && <div className="callout error">{previewError}</div>}
      {preview?.blockers.map((b) => (
        <div key={b} className="callout error">
          {b}
        </div>
      ))}
      {preview?.warnings.map((w) => (
        <div key={w} className="callout warn">
          {w}
        </div>
      ))}

      {preview && (
        <div className={`plan ${loading ? 'stale' : ''}`}>
          <div className="plan-summary">
            <span>
              <strong>{preview.inline.length}</strong> inline comment{preview.inline.length === 1 ? '' : 's'}
            </span>
            <span>
              <strong>{preview.general.length}</strong> note{preview.general.length === 1 ? '' : 's'} in the review text
            </span>
            {preview.skippedResolved > 0 && <span className="muted">{preview.skippedResolved} resolved skipped</span>}
            {preview.alreadyPublished > 0 && <span className="muted">{preview.alreadyPublished} posted earlier</span>}
            <span className="spacer" />
            <button className="btn-link small" onClick={() => setShowDetails((s) => !s)}>
              {showDetails ? 'Hide details' : 'Show what will be posted'}
            </button>
          </div>
          {showDetails && (
            <div className="plan-details">
              {preview.inline.length > 0 && (
                <>
                  <div className="plan-heading">Inline on the diff</div>
                  <ul>
                    {preview.inline.map((c) => (
                      <li key={c.noteId}>
                        <KindBadge kind={c.kind} />{' '}
                        <span className="mono small">
                          {c.path}:{c.startLine}
                          {c.endLine !== c.startLine ? `-${c.endLine}` : ''}
                          {c.side === 'Old' ? ' (removed)' : ''}
                        </span>{' '}
                        <span className="ellipsis-2">{c.body}</span>
                      </li>
                    ))}
                  </ul>
                </>
              )}
              {preview.general.length > 0 && (
                <>
                  <div className="plan-heading">In the review text (can't be placed on the final diff)</div>
                  <ul>
                    {preview.general.map((g) => (
                      <li key={g.noteId}>
                        <KindBadge kind={g.kind} />{' '}
                        {g.path && (
                          <span className="mono small">
                            {g.path}
                            {g.line ? `:${g.line}` : ''}
                          </span>
                        )}{' '}
                        <span className="muted small">— {g.reason}</span>
                      </li>
                    ))}
                  </ul>
                </>
              )}
              <div className="plan-heading">Review text</div>
              <pre className="export-preview">{preview.body}</pre>
            </div>
          )}
        </div>
      )}

      <div className="row publish-actions">
        <label className="small">
          <input type="checkbox" checked={includeResolved} onChange={(e) => setIncludeResolved(e.target.checked)} /> include resolved notes
        </label>
        {(preview?.alreadyPublished ?? 0) > 0 && (
          <label className="small">
            <input type="checkbox" checked={republish} onChange={(e) => setRepublish(e.target.checked)} /> post earlier notes again
          </label>
        )}
        <span className="spacer" />
        <button
          className="btn primary"
          disabled={!preview || preview.blockers.length > 0 || !preview.pullRequest || posting || loading || !!nothingToPost}
          title={nothingToPost ? 'Nothing new to post' : undefined}
          onClick={post}
        >
          {posting ? 'Posting…' : `Post review to ${target!.providerName}`}
        </button>
      </div>
      {postError && <div className="callout error">{postError}</div>}
      {lastResult && (
        <div className="callout ok">
          Posted.{' '}
          <a href={lastResult.url} target="_blank" rel="noreferrer">
            Open on {target!.providerName} ↗
          </a>
          {lastResult.warnings.map((w) => (
            <div key={w} className="small">
              {w}
            </div>
          ))}
        </div>
      )}
      {published.length > 0 && (
        <div className="published-list">
          <div className="plan-heading">Posted before</div>
          {[...published].reverse().map((p) => (
            <a key={p.publishedAt} href={p.url} target="_blank" rel="noreferrer" className="small">
              {p.repository}#{p.pullRequestNumber} · {p.verdict === 'RequestChanges' ? 'requested changes' : p.verdict.toLowerCase()} · {p.noteIds.length} note
              {p.noteIds.length === 1 ? '' : 's'} ({p.inlineComments} inline) · {relativeTime(p.publishedAt)} ↗
            </a>
          ))}
        </div>
      )}
    </div>
  );
}
