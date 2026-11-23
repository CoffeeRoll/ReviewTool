/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The ASP.NET Core server serves the built app from its wwwroot folder.
// During development, `npm run dev` proxies API calls to the server.
export default defineConfig({
  plugins: [react()],
  base: './',
  build: {
    outDir: '../ReviewTool.Server/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 1500,
  },
  // ES-module workers can code-split, so each language grammar is its own file, loaded only when a
  // file in that language is opened (instead of one huge worker bundle).
  worker: {
    format: 'es',
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: process.env.REVIEWTOOL_API ?? 'http://localhost:5178', changeOrigin: true },
    },
  },
  test: {
    environment: 'node',
  },
});
