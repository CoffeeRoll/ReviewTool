import { useEffect, useMemo, useState } from 'react';
import { ThemeToggle } from '../components/Controls';
import { ProgressRing } from '../components/Sidebar';
import { api } from '../lib/api';
import { relativeTime, shortSha } from '../lib/diff';
import { REVIEW_STATUSES, type CommitInfo, type RepoInfo, type RepoRefs, type ReviewSummary } from '../lib/types';

const RECENT_KEY = 'reviewtool.recentRepos';

function recentRepos(): string[] {
  try {
    return JSON.parse(localStorage.getItem(RECENT_KEY) ?? '[]');
  } catch {
    return [];
  }
}

function rememberRepo(path: string) {
  try {
    localStorage.setItem(RECENT_KEY, JSON.stringify([path, ...recentRepos().filter((p) => p !== path)].slice(0, 10)));
  } catch {
    /* ignore */
  }
}

export function HomePage() {
  const [reviews, setReviews] = useState<ReviewSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = () => api.listReviews().then(setReviews, (e: Error) => setError(e.message));
  useEffect(() => {
    load();
  }, []);

  return (
    <div className="home">
      <header className="home-head">
        <div>
          <h1>ReviewTool</h1>
          <p className="muted">Review pull requests one commit at a time.</p>
        </div>
        <ThemeToggle />
      </header>
      <NewReviewForm reviewRepos={reviews?.map((r) => r.repoPath) ?? []} />
      <section className="card">
        <h2>Reviews</h2>
        {error && <p className="error">{error}</p>}
        {!reviews && !error && <p className="muted">Loading…</p>}
        {reviews?.length === 0 && <p className="muted">No reviews yet. Start one above.</p>}
        <ul className="review-list">
          {reviews?.map((r) => (
            <ReviewRow
              key={r.id}
              review={r}
              onDelete={async () => {
                if (!confirm(`Delete review "${r.name}" and all of its notes?`)) return;
                await api.deleteReview(r.id);
                load();
              }}
            />
          ))}
        </ul>
      </section>
    </div>
  );
}

function ReviewRow({ review: r, onDelete }: { review: ReviewSummary; onDelete: () => void }) {
  const status = REVIEW_STATUSES.find((s) => s.value === r.status)?.label ?? r.status;
  const pct = r.fileCount === 0 ? 100 : Math.round((r.reviewedFileCount / r.fileCount) * 100);
  return (
    <li className="review-row">
      <a href={`#/review/${r.id}`} className="review-row-main">
        <ProgressRing value={r.reviewedFileCount} total={r.fileCount} size={30} />
        <div className="review-row-text">
          <div className="review-row-title">
            <strong>{r.name}</strong>
            <span className={`badge status-${r.status.toLowerCase()}`}>{status}</span>
          </div>
          <div className="muted small ellipsis">
            <span className="mono">{r.baseRef}</span> ← <span className="mono">{r.headRef}</span> ({shortSha(r.headSha)}) · {r.repoPath}
          </div>
          <div className="small">
            {r.reviewedCommitCount}/{r.commitCount} commits · {r.reviewedFileCount}/{r.fileCount} files ({pct}%) · {r.openNoteCount} open note
            {r.openNoteCount === 1 ? '' : 's'} · updated {relativeTime(r.updatedAt)}
          </div>
        </div>
      </a>
      <div className="review-row-actions">
        <a className="btn primary" href={`#/review/${r.id}`}>
          {r.reviewedFileCount > 0 ? 'Resume' : 'Start'}
        </a>
        <a className="btn" href={api.exportUrl(r.id)} target="_blank" rel="noreferrer" title="Notes as Markdown">
          Export
        </a>
        <button className="btn danger" onClick={onDelete}>
          Delete
        </button>
      </div>
    </li>
  );
}

type Mode = 'branches' | 'pr';

function NewReviewForm({ reviewRepos }: { reviewRepos: string[] }) {
  const recents = useMemo(() => [...new Set([...recentRepos(), ...reviewRepos])], [reviewRepos]);
  const [path, setPath] = useState(recents[0] ?? '');
  const [repo, setRepo] = useState<{ info: RepoInfo; refs: RepoRefs } | null>(null);
  const [mode, setMode] = useState<Mode>('branches');
  const [base, setBase] = useState('');
  const [head, setHead] = useState('');
  const [prNumber, setPrNumber] = useState('');
  const [remote, setRemote] = useState('origin');
  const [provider, setProvider] = useState('github');
  const [name, setName] = useState('');
  const [preview, setPreview] = useState<{ commits?: CommitInfo[]; error?: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const loadRepo = async (p = path) => {
    setError(null);
    setRepo(null);
    if (!p.trim()) return;
    try {
      const [info, refs] = await Promise.all([api.repoInfo(p), api.repoRefs(p)]);
      setRepo({ info, refs });
      setPath(info.path);
      setBase(refs.defaultBranch ?? '');
      setHead(refs.currentBranch && refs.currentBranch !== refs.defaultBranch ? refs.currentBranch : '');
      if (info.remotes.length && !info.remotes.includes('origin')) setRemote(info.remotes[0]);
    } catch (e) {
      setError((e as Error).message);
    }
  };

  useEffect(() => {
    if (path) loadRepo(path);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Live preview of the commits that will be reviewed.
  useEffect(() => {
    if (!repo || mode !== 'branches' || !base.trim() || !head.trim()) {
      setPreview(null);
      return;
    }
    const ctrl = new AbortController();
    const t = setTimeout(() => {
      api.repoCommits(repo.info.path, base, head, ctrl.signal).then(
        (commits) => setPreview({ commits }),
        (e: Error) => e.name !== 'AbortError' && setPreview({ error: e.message }),
      );
    }, 300);
    return () => {
      clearTimeout(t);
      ctrl.abort();
    };
  }, [repo, mode, base, head]);

  const create = async () => {
    if (!repo) return;
    setBusy(true);
    setError(null);
    try {
      const review = await api.createReview({
        repoPath: repo.info.path,
        baseRef: base || undefined,
        headRef: mode === 'branches' ? head || undefined : undefined,
        name: name || undefined,
        pullRequest: mode === 'pr' ? { number: Number(prNumber), remote, provider } : undefined,
      });
      rememberRepo(repo.info.path);
      location.hash = `#/review/${review.id}`;
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const refNames = repo?.refs.refs.map((r) => r.name) ?? [];
  const canCreate = !!repo && !!base && (mode === 'branches' ? !!head && !preview?.error && preview?.commits?.length !== 0 : Number(prNumber) > 0);

  return (
    <section className="card">
      <h2>New review</h2>
      <div className="form-grid">
        <label className="span-2">
          Repository folder
          <div className="row">
            <input
              className="input mono"
              list="recent-repos"
              placeholder="/path/to/your/repo"
              value={path}
              onChange={(e) => setPath(e.target.value)}
              onKeyDown={(e) => e.key === 'Enter' && loadRepo()}
              onBlur={() => !repo && path && loadRepo()}
            />
            <button className="btn" onClick={() => loadRepo()}>
              Open
            </button>
          </div>
          <datalist id="recent-repos">
            {recents.map((r) => (
              <option key={r} value={r} />
            ))}
          </datalist>
        </label>
      </div>
      {repo && (
        <>
          <div className="seg tabs">
            <button className={mode === 'branches' ? 'on' : ''} onClick={() => setMode('branches')}>
              Compare branches
            </button>
            <button className={mode === 'pr' ? 'on' : ''} onClick={() => setMode('pr')}>
              Fetch a pull request
            </button>
          </div>
          <datalist id="refs">
            {refNames.map((r) => (
              <option key={r} value={r} />
            ))}
          </datalist>
          <div className="form-grid">
            <label>
              Base (target branch)
              <input className="input mono" list="refs" value={base} onChange={(e) => setBase(e.target.value)} placeholder="main" />
            </label>
            {mode === 'branches' ? (
              <label>
                Head (branch with the changes)
                <input className="input mono" list="refs" value={head} onChange={(e) => setHead(e.target.value)} placeholder="feature/my-change" />
              </label>
            ) : (
              <>
                <label>
                  PR number
                  <input className="input" type="number" min={1} value={prNumber} onChange={(e) => setPrNumber(e.target.value)} placeholder="123" />
                </label>
                <label>
                  Remote
                  <select className="input" value={remote} onChange={(e) => setRemote(e.target.value)}>
                    {repo.info.remotes.length === 0 && <option value="">(no remotes)</option>}
                    {repo.info.remotes.map((r) => (
                      <option key={r}>{r}</option>
                    ))}
                  </select>
                </label>
                <label>
                  Host
                  <select className="input" value={provider} onChange={(e) => setProvider(e.target.value)}>
                    <option value="github">GitHub / Gitea / Bitbucket Server (pull/N/head)</option>
                    <option value="gitlab">GitLab (merge-requests/N/head)</option>
                  </select>
                </label>
              </>
            )}
            <label>
              Name (optional)
              <input className="input" value={name} onChange={(e) => setName(e.target.value)} placeholder="Defaults to head → base" />
            </label>
          </div>
          {mode === 'pr' && <p className="muted small">The PR head is fetched with git into <code>refs/reviewtool/…</code>, using your existing git credentials.</p>}
          {preview?.error && <p className="error small">{preview.error}</p>}
          {preview?.commits && (
            <div className="preview">
              <strong>{preview.commits.length === 500 ? '500+' : preview.commits.length}</strong> commit{preview.commits.length === 1 ? '' : 's'} to review
              {preview.commits.length > 0 && (
                <ol>
                  {preview.commits.slice(0, 8).map((c) => (
                    <li key={c.sha}>
                      <span className="mono muted">{shortSha(c.sha)}</span> {c.subject}
                    </li>
                  ))}
                  {preview.commits.length > 8 && <li className="muted">…and {preview.commits.length - 8} more</li>}
                </ol>
              )}
            </div>
          )}
        </>
      )}
      {error && <p className="error">{error}</p>}
      <div className="row end">
        <button className="btn primary" disabled={!canCreate || busy} onClick={create}>
          {busy ? 'Creating…' : 'Start review'}
        </button>
      </div>
    </section>
  );
}
