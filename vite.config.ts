import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { VitePWA } from 'vite-plugin-pwa';

export default defineConfig({
  base: './',
  plugins: [
    react(),
    VitePWA({
      registerType: 'prompt',
      includeAssets: ['apple-touch-icon.png', 'pwa-192x192.png', 'pwa-512x512.png'],
      manifest: {
        name: 'LocalMorph',
        short_name: 'LocalMorph',
        description:
          'Convert images, audio, and video on your device in three clear steps with no managed-server uploads.',
        theme_color: '#0B172A',
        background_color: '#F3F6FA',
        display: 'standalone',
        categories: ['utilities', 'productivity'],
        scope: './',
        start_url: './#/app',
        icons: [
          {
            src: 'pwa-192x192.png',
            sizes: '192x192',
            type: 'image/png',
          },
          {
            src: 'pwa-512x512.png',
            sizes: '512x512',
            type: 'image/png',
          },
          {
            src: 'pwa-512x512.png',
            sizes: '512x512',
            type: 'image/png',
            purpose: 'maskable',
          },
        ],
      },
      workbox: {
        globPatterns: ['**/*.{js,css,html,ico,png,svg,woff,woff2}'],
        runtimeCaching: [],
        navigateFallbackDenylist: [/sitemap\.xml$/, /robots\.txt$/, /appcast(-windows)?\.xml$/],
      },
    }),
  ],
});
