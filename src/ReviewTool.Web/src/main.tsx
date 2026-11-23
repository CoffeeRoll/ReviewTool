import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { applyStoredBrackets, applyStoredDensity, applyStoredTheme } from './components/Controls';
import { api } from './lib/api';
import { setLanguageOverrides } from './lib/highlight';
import './styles.css';

applyStoredTheme();
applyStoredDensity();
applyStoredBrackets();

const render = () =>
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );

// Load configured file-type → language mappings before the first diff renders (fast: it's local).
api
  .settings()
  .then((s) => setLanguageOverrides(s.languages))
  .catch(() => undefined)
  .finally(render);
