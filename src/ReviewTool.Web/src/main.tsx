import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { applyStoredDensity, applyStoredTheme } from './components/Controls';
import './styles.css';

applyStoredTheme();
applyStoredDensity();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
