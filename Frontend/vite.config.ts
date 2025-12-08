import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { version } from './package.json';
import tailwindcss from '@tailwindcss/vite';

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 2882,
    open: false,
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
      },
    },
    // Ensure no caching issues by generating proper cache headers
    manifest: true,
  },
});
