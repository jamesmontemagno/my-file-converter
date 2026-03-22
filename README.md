# My File Converter

A static, client-side file converter website for image, audio, and video formats.

## What this starter includes

- Native-first conversion routing:
  - Image: `canvas.toBlob()`
  - Audio/Video: `MediaRecorder` with `captureStream` when supported
- Runtime capability detection panel (MediaRecorder, WebCodecs presence, image MIME checks)
- Worker-based WASM fallback scaffold for `ffmpeg.wasm` integration
- GitHub Pages deployment workflow

## Important limitations

- This project ships a **WASM integration scaffold**, not a bundled ffmpeg core.
- GitHub Pages cannot set custom COOP/COEP headers easily for this site, so this starter is designed for **single-thread fallback**.
- Native audio/video conversion support varies by browser MIME support and `captureStream` behavior.

## Local development

Because this is a static starter, you can run any static server:

```bash
python3 -m http.server 4173
```

Then open `http://localhost:4173`.

## Deploy to GitHub Pages

1. Push this repository to GitHub.
2. In repo settings, enable **Pages** with **GitHub Actions** as source.
3. The included workflow publishes on pushes to `main`.

## Manual browser smoke checklist

Run these checks after deployment in Chromium, Firefox, and Safari:

1. Open site and verify runtime support panel renders without errors.
2. Convert PNG/JPEG/WebP image with native route.
3. Convert small audio/video file where MediaRecorder target MIME is reported supported.
4. Attempt an unsupported target; verify fallback warning appears.
5. Enable WASM fallback and provide module URL; verify worker route runs.

## WASM fallback integration contract

If you provide a module URL in the app, the worker expects:

```js
export async function convert({ file, targetMime, onProgress }) {
  // ...your ffmpeg logic...
  return { blob, outputName };
}
```

The worker file is `src/conversion-worker.js`.
