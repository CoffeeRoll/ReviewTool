import { useEffect, useMemo, useState } from 'react';
import { api } from '../lib/api';
import { relativeTime } from '../lib/diff';
import type { OpenPullRequestEntry, OpenPullRequestList, RepoInfo } from '../lib/types';
import { ProgressRing } from './Sidebar';

/**
 * Open pull requests in the selected repository, newest first, with a search box. Clicking one starts
 * a review of it (fetching the PR), or resumes the review already started.
 */
export function OpenPullRequests({ repo }: { repo: RepoInfo }) {
  const [remote, setRemote] = useState<string | null>(null);
  const [list, setList] = useState<OpenPullRequestList | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [query, setQuery] = useState('');
  const [starting, setStarting] = useState<number | null>(null);
  const [reloadKey, setReloadKey] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    api.openPullRequests(repo.path, remote).then(
      (l) => {
        if (cancelled) return;
        setList(l);
        setLoading(false);
      },
      (e: Error) => {
        if (cancelled) return;
        setError(e.message);
        setLoading(false);
      },
    );
    return () => {
      cancelled = true;
    };
  }, [repo.path, remote, reloadKey]);

  const shown = useMemo(() => {
    const prs = list?.pullRequests ?? [];
    const q = query.trim().toLowerCase().replace(/^#/, '');
    if (!q) return prs;
    return prs.filter(
      (p) =>
        String(p.number).startsWith(q) ||
        (p.title ?? '').toLowerCase().includes(q) ||
        (p.author ?? '').toLowerCase().includes(q) ||
        (p.headRef ?? '').toLowerCase().includes(q),
    );
  }, [list, query]);

  const open = async (pr: OpenPullRequestEntry) => {
    if (pr.reviewId) {
      location.hash = `#/review/${pr.reviewId}`;
      return;
    }
    const prRemote = list?.remote ?? remote ?? 'origin';
    setStarting(pr.number);
    setError(null);
    try {
      const { review } = await api.openReview({
        repoPath: repo.path,
        // The PR's own base branch (fetched fresh by the server); falls back to the default branch.
        baseRef: pr.baseRef ? `${prRemote}/${pr.baseRef}` : undefined,
        pullRequest: { number: pr.number, remote: prRemote },
      });
      location.hash = `#/review/${review.id}`;
    } catch (e) {
      setError(`Couldn't start a review of #${pr.number}: ${(e as Error).message}`);
      setStarting(null);
    }
  };

  const source =
    list?.source === 'git'
      ? `from the pull request refs on ${list.remote ?? 'the remote'}`
      : list
        ? `from ${list.sourceName}${list.repository ? ` · ${list.repository}` : ''}`
        : '';

  return (
    <section className="card open-prs">
      <div className="open-prs-head">
        <h2>
          Open pull requests{' '}
          {list && (
            <span className="muted small">
              {list.pullRequests.length} {source}
            </span>
          )}
        </h2>
        <span className="spacer" />
        {repo.remotes.length > 1 && (
          <select className="input small" value={remote ?? list?.remote ?? ''} onChange={(e) => setRemote(e.target.value)} title="Remote to list pull requests from">
            {repo.remotes.map((r) => (
              <option key={r}>{r}</option>
            ))}
          </select>
        )}
        <button className="btn small" onClick={() => setReloadKey((k) => k + 1)} disabled={loading} title="Reload the list">
          {loading ? 'Loading…' : '⟳'}
        </button>
      </div>
      <input
        className="input"
        type="search"
        placeholder="Search by number, title, author or branch…"
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        aria-label="Search pull requests"
      />
      {list?.note && <p className="callout warn small">{list.note}</p>}
      {error && <p className="callout error small">{error}</p>}
      <ul className="pr-list" aria-label="Open pull requests">
        {!list && loading && <li className="muted pad">Loading pull requests…</li>}
        {list && list.pullRequests.length === 0 && !list.note && <li className="muted pad">No open pull requests.</li>}
        {list && list.pullRequests.length > 0 && shown.length === 0 && <li className="muted pad">No pull requests match “{query}”.</li>}
        {shown.map((pr) => (
          <li key={pr.number}>
            <button className="pr-row" onClick={() => open(pr)} disabled={starting != null} title={pr.reviewId ? 'Resume your review' : 'Start reviewing this pull request'}>
              <span className="pr-number mono">#{pr.number}</span>
              <span className="pr-main">
                <span className="pr-title">
                  {pr.title ?? <span className="muted">Pull request #{pr.number}</span>}
                  {pr.draft && <span className="pill">draft</span>}
                </span>
                <span className="pr-meta muted small">
                  {pr.author && <>{pr.author} · </>}
                  {pr.headRef && (
                    <span className="mono">
                      {pr.headRef} → {pr.baseRef}
                    </span>
                  )}
                  {(pr.updatedAt ?? pr.createdAt) && <> · updated {relativeTime((pr.updatedAt ?? pr.createdAt)!)}</>}
                </span>
              </span>
              {starting === pr.number ? (
                <span className="muted small">Fetching…</span>
              ) : pr.reviewId ? (
                <span className="pr-progress" title={`${pr.reviewedFiles}/${pr.totalFiles} file changes reviewed`}>
                  <ProgressRing value={pr.reviewedFiles} total={pr.totalFiles} />
                  <span className="small">Resume</span>
                </span>
              ) : (
                <span className="small pr-start">Review →</span>
              )}
            </button>
            {pr.url && (
              <a className="icon-btn pr-link" href={pr.url} target="_blank" rel="noreferrer" title="Open on the hosting site" aria-label={`Open #${pr.number} in the browser`}>
                ↗
              </a>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}
