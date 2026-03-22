# LocalMorph

A React + TypeScript + Vite client-side file converter for GitHub Pages.

## Stack

- React 19
- TypeScript
- Vite
- Native browser conversion paths first
- `ffmpeg.wasm` single-thread fallback for unsupported conversions

## Features

- Dedicated landing page that explains privacy, performance, and the conversion flow
- Separate converter workspace focused on upload, route clarity, status, and preview
- Built-in Privacy Policy and Terms of Use pages for static deployments
- Image conversion via Canvas export
- Audio/video conversion via `MediaRecorder` when supported by the browser
- Real `ffmpeg.wasm` fallback module (`src/ffmpeg-module.ts`)
- Worker-based background processing
- GitHub Pages deployment workflow

## Local development

```bash
npm install
npm run dev
```

## Production build

```bash
npm run build
```

## Deployment

Push to `main` and GitHub Actions will build and publish `dist/` to GitHub Pages.

## Branding and legal pages

The app is branded as `LocalMorph` and includes hash-routed `Privacy Policy` and `Terms of Use`
pages so they work on static hosting without additional server routes.

## Manual smoke checklist

1. Run `npm run dev` and verify the app loads.
2. Convert PNG/JPEG/WebP image through the native route.
3. Convert a small audio/video file where `MediaRecorder` support is reported.
4. Force an unsupported conversion and verify the ffmpeg fallback path runs.
5. Run the deployed site in Chromium, Firefox, and Safari.

## Known constraints

- MediaRecorder-based audio/video conversion remains browser-dependent.
- `ffmpeg.wasm` has a large first-load cost and is slower than native ffmpeg.
- GitHub Pages hosting means this app uses the single-thread ffmpeg core.
- The included privacy and terms copy is product-facing starter content and should be reviewed
  before production/legal use.
