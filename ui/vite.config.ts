/// <reference types="vitest/config" />
import { fileURLToPath, URL } from 'node:url';
import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// The gateway serves the built UI from its client listener, so production is same-origin.
// In development Vite proxies the API to a local gateway (see docs/ui.md).
const gateway = process.env.SHARPCLAW_GATEWAY ?? 'http://localhost:8080';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      // Radix's scroll lock injects a <style> element, which the gateway's CSP (style-src 'self')
      // blocks. This shim locks scrolling through the CSSOM instead. See docs/ui.md.
      'react-remove-scroll': fileURLToPath(new URL('./src/lib/removeScroll.tsx', import.meta.url)),
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: gateway, changeOrigin: false },
      '/openapi': { target: gateway, changeOrigin: false },
      '/healthz': { target: gateway, changeOrigin: false },
    },
  },
  build: {
    outDir: 'dist',
    // No inline scripts may reach index.html (CSP script-src 'self').
    modulePreload: { polyfill: false },
    assetsInlineLimit: 0,
    sourcemap: false,
    // scripts/check-dist.mjs enforces the real budget: 250 KB gzipped of initial JavaScript.
    chunkSizeWarningLimit: 1024,
  },
  test: {
    environment: 'jsdom',
    environmentOptions: { jsdom: { url: 'http://localhost:5173/' } },
    globals: false,
    setupFiles: ['./tests/setup.ts'],
    include: ['tests/**/*.test.{ts,tsx}'],
    css: false,
    restoreMocks: true,
  },
});
