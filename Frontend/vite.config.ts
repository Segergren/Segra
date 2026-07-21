import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { version as pkgVersion } from './package.json';
import tailwindcss from '@tailwindcss/vite';

// Packaged builds pass the real release version via SEGRA_VERSION so the frontend's __APP_VERSION__
// matches the backend's packaged version (used for "What's New" release-note gating). A bare local
// build has no SEGRA_VERSION and falls back to package.json ("Developer Preview").
const env = (globalThis as { process?: { env?: Record<string, string | undefined> } }).process?.env;
const version = env?.SEGRA_VERSION || pkgVersion;

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  legacy: {
    // react-use-websocket exposes its hook via CJS `exports.default`; Rolldown's
    // stricter interop in Vite 8 returns undefined. Drop when the lib ships ESM.
    inconsistentCjsInterop: true,
  },
  server: {
    port: 2882,
  },
  define: {
    __APP_VERSION__: JSON.stringify(version),
  },
  build: {
    // Add cache busting for assets with content hashing
    rollupOptions: {
      output: {
        entryFileNames: 'assets/[name].[hash].js',
        chunkFileNames: 'assets/[name].[hash].js',
        assetFileNames: 'assets/[name].[hash].[ext]',
        manualChunks: (id) => {
          if (!id.includes('node_modules')) return;
          if (id.includes('framer-motion')) return 'framer-motion';
          if (id.includes('mp4box')) return 'mp4box';
          if (id.includes('react-dom')) return 'react-dom';
          if (id.includes('react-dnd')) return 'react-dnd';
          if (id.includes('lucide')) return 'lucide';
          if (id.includes('@tanstack')) return 'tanstack';
          return 'vendor';
        },
      },
    },
    // Ensure no caching issues by generating proper cache headers
    manifest: true,
  },
});
