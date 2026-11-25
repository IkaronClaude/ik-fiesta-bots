import { defineConfig } from 'vite';

// No React plugin: esbuild compiles TSX (automatic runtime). Fewer dependencies; no fast refresh needed.
// Dev: `npm run mock` (port 5199) + `npm run dev`; the dev server proxies /api to BOT_HOST (default the mock).
const target = process.env.BOT_HOST || 'http://127.0.0.1:5199';

export default defineConfig({
  base: './',
  esbuild: { jsx: 'automatic' },
  server: {
    port: 5198,
    strictPort: true,
    proxy: { '/api': { target, changeOrigin: true, ws: true } },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    chunkSizeWarningLimit: 4000,
    sourcemap: false,
  },
  worker: { format: 'es' },
});
