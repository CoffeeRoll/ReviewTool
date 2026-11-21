import { useEffect, useState } from 'react';
import { HomePage } from './pages/HomePage';
import { ReviewPage } from './pages/ReviewPage';

function useHashRoute() {
  const [hash, setHash] = useState(() => location.hash);
  useEffect(() => {
    const onChange = () => setHash(location.hash);
    window.addEventListener('hashchange', onChange);
    return () => window.removeEventListener('hashchange', onChange);
  }, []);
  return hash.replace(/^#\/?/, '');
}

export function App() {
  const route = useHashRoute();
  const m = /^review\/([A-Za-z0-9_-]+)/.exec(route);
  useEffect(() => {
    if (!m) document.title = 'ReviewTool';
  }, [m]);
  if (m) return <ReviewPage id={m[1]} />;
  // "#/?repo=/path" (used by the reviewtool command) pre-fills the new-review form.
  const repo = new URLSearchParams(route.split('?')[1] ?? '').get('repo') ?? undefined;
  return <HomePage key={repo ?? ''} initialRepo={repo} />;
}
