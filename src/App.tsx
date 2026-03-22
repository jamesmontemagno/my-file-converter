import { useEffect, useMemo, useState } from 'react';
import {
  classifyMediaType,
  detectCapabilities,
  targetFormatsFor,
  type CapabilityReport,
  type MediaKind,
} from './capabilities';
import {
  convertImage,
  convertViaMediaRecorder,
  supportsNativeRoute,
  type ConversionResult,
} from './conversion';
import { convertWithWasmFallback } from './worker-client';

type Page = 'landing' | 'app';

function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`;
}

function getPageFromHash(): Page {
  return window.location.hash === '#/app' ? 'app' : 'landing';
}

function routeLabel(route: 'native' | 'wasm' | 'blocked') {
  if (route === 'native') return 'Native browser path';
  if (route === 'wasm') return 'ffmpeg.wasm fallback';
  return 'Unsupported until fallback is enabled';
}

function previewForResult(downloadUrl: string, result: ConversionResult) {
  if (result.blob.type.startsWith('image/')) {
    return <img className="preview-image" src={downloadUrl} alt={result.outputName} />;
  }

  if (result.blob.type.startsWith('audio/')) {
    return <audio className="preview-media" src={downloadUrl} controls />;
  }

  if (result.blob.type.startsWith('video/')) {
    return <video className="preview-media" src={downloadUrl} controls playsInline />;
  }

  return <p className="muted">Preview is not available for this output type.</p>;
}

function LandingPage({ onOpenApp }: { onOpenApp: () => void }) {
  return (
    <main className="page">
      <header className="topbar">
        <div className="brand">My File Converter</div>
        <button className="ghost-button" onClick={onOpenApp}>
          Open app
        </button>
      </header>

      <section className="hero">
        <div className="hero-copy">
          <span className="eyebrow">Private. Fast. Browser-native.</span>
          <h1>Convert video, audio, and images without uploading your files.</h1>
          <p className="hero-text">
            A modern converter that uses native browser APIs first and falls back to
            `ffmpeg.wasm` only when needed. The UX is designed so users always know what is
            happening, which route is being used, and what they can do next.
          </p>
          <div className="hero-actions">
            <button onClick={onOpenApp}>Start converting</button>
            <a className="text-link" href="#features">
              Explore features
            </a>
          </div>
          <div className="hero-badges">
            <span>Client-side processing</span>
            <span>Image, audio, video</span>
            <span>Preview after conversion</span>
          </div>
        </div>

        <div className="hero-panel">
          <div className="hero-stat">
            <strong>Native-first routing</strong>
            <p>Use fast built-in browser capabilities whenever possible.</p>
          </div>
          <div className="hero-stat">
            <strong>Fallback when needed</strong>
            <p>Automatic ffmpeg path for broader format coverage.</p>
          </div>
          <div className="hero-stat">
            <strong>Clear status and output</strong>
            <p>Progress, route visibility, and visual output preview built in.</p>
          </div>
        </div>
      </section>

      <section id="features" className="feature-grid">
        <article className="feature-card">
          <h2>Confidence through clarity</h2>
          <p>
            Users can see supported routes, current progress, selected formats, and whether the
            app is using the native or ffmpeg pipeline.
          </p>
        </article>
        <article className="feature-card">
          <h2>Designed for GitHub Pages</h2>
          <p>
            Static deployment, React + TypeScript + Vite build, and a single-thread fallback path
            that works without special server runtime infrastructure.
          </p>
        </article>
        <article className="feature-card">
          <h2>Better post-conversion workflow</h2>
          <p>
            Converted output is previewed inline so people can validate the result before they
            download it.
          </p>
        </article>
      </section>

      <section className="workflow">
        <h2>How the experience should feel</h2>
        <div className="workflow-steps">
          <div>
            <span>1</span>
            <h3>Choose a file</h3>
            <p>We detect what it is and show the formats that make sense.</p>
          </div>
          <div>
            <span>2</span>
            <h3>See the route</h3>
            <p>Users know before converting whether the browser can do it natively.</p>
          </div>
          <div>
            <span>3</span>
            <h3>Watch progress</h3>
            <p>Clear status messaging and progress updates reduce uncertainty.</p>
          </div>
          <div>
            <span>4</span>
            <h3>Preview the result</h3>
            <p>Image, audio, and video output is visualized directly in the app.</p>
          </div>
        </div>
      </section>

      <section className="cta-band">
        <h2>Ready to try the full converter experience?</h2>
        <button onClick={onOpenApp}>Go to the app workspace</button>
      </section>
    </main>
  );
}

export default function App() {
  const [page, setPage] = useState<Page>(getPageFromHash());
  const [file, setFile] = useState<File | null>(null);
  const [quality, setQuality] = useState(0.9);
  const [status, setStatus] = useState('Select a file to begin.');
  const [progress, setProgress] = useState(0);
  const [enableWasmFallback, setEnableWasmFallback] = useState(true);
  const [customModuleUrl, setCustomModuleUrl] = useState('');
  const [result, setResult] = useState<ConversionResult | null>(null);
  const [downloadUrl, setDownloadUrl] = useState('');
  const [capabilities, setCapabilities] = useState<CapabilityReport | null>(null);
  const [busy, setBusy] = useState(false);

  const mediaType: MediaKind = classifyMediaType(file);
  const targetOptions = useMemo(() => targetFormatsFor(mediaType), [mediaType]);
  const [targetMime, setTargetMime] = useState('');

  useEffect(() => {
    const onHashChange = () => setPage(getPageFromHash());
    window.addEventListener('hashchange', onHashChange);
    return () => window.removeEventListener('hashchange', onHashChange);
  }, []);

  useEffect(() => {
    setCapabilities(detectCapabilities());
  }, []);

  useEffect(() => {
    setTargetMime(targetOptions[0]?.value ?? '');
  }, [targetOptions]);

  useEffect(() => {
    if (!result) {
      setDownloadUrl('');
      return undefined;
    }

    const url = URL.createObjectURL(result.blob);
    setDownloadUrl(url);
    return () => URL.revokeObjectURL(url);
  }, [result]);

  const defaultModuleUrl = new URL('./ffmpeg-module.ts', import.meta.url).href;
  const moduleUrl = customModuleUrl.trim() || defaultModuleUrl;

  const routeDecision = useMemo(() => {
    if (!file || !targetMime) return 'blocked' as const;
    if (supportsNativeRoute(file, targetMime)) return 'native' as const;
    if (enableWasmFallback) return 'wasm' as const;
    return 'blocked' as const;
  }, [enableWasmFallback, file, targetMime]);

  const steps = [
    { label: 'Upload', state: file ? 'done' : 'current' },
    { label: 'Configure', state: file && targetMime ? 'done' : 'pending' },
    { label: 'Convert', state: busy ? 'current' : result ? 'done' : 'pending' },
    { label: 'Preview & download', state: result ? 'done' : 'pending' },
  ];

  function navigate(next: Page) {
    window.location.hash = next === 'app' ? '/app' : '';
    setPage(next);
  }

  function handleProgress(nextProgress: number, message: string) {
    setProgress(Math.max(0, Math.min(1, nextProgress)));
    setStatus(message);
  }

  async function runConversion() {
    if (!file || !targetMime) return;

    setBusy(true);
    setResult(null);
    handleProgress(0.05, 'Preparing conversion job');

    try {
      let next: ConversionResult;

      if (supportsNativeRoute(file, targetMime)) {
        next =
          mediaType === 'image'
            ? await convertImage({ file, targetMime, quality, onProgress: handleProgress })
            : await convertViaMediaRecorder({ file, targetMime, onProgress: handleProgress });
      } else if (enableWasmFallback) {
        next = await convertWithWasmFallback({
          file,
          targetMime,
          wasmModuleUrl: moduleUrl,
          onProgress: handleProgress,
        });
      } else {
        throw new Error('Native route is unsupported and ffmpeg fallback is disabled.');
      }

      setResult(next);
      handleProgress(1, 'Conversion complete — preview ready below');
    } catch (error) {
      handleProgress(
        0,
        `Conversion failed: ${error instanceof Error ? error.message : 'Unknown error'}`,
      );
    } finally {
      setBusy(false);
    }
  }

  if (page === 'landing') {
    return <LandingPage onOpenApp={() => navigate('app')} />;
  }

  return (
    <main className="page">
      <header className="topbar">
        <div className="brand">My File Converter</div>
        <nav className="nav-actions">
          <button className="ghost-button" onClick={() => navigate('landing')}>
            Back to landing
          </button>
        </nav>
      </header>

      <section className="workspace-hero">
        <div>
          <span className="eyebrow">Converter workspace</span>
          <h1>Convert locally with clear status and immediate preview.</h1>
          <p className="hero-text compact">
            Choose a file, select an output, and the app will tell you exactly how it plans to
            convert it before you start.
          </p>
        </div>
        <div className={`route-chip route-${routeDecision}`}>{routeLabel(routeDecision)}</div>
      </section>

      <section className="workspace-grid">
        <aside className="panel stack">
          <div className="card">
            <h2>1. Upload and configure</h2>
            <label className="field">
              <span>Input file</span>
              <input
                type="file"
                disabled={busy}
                onChange={(event) => {
                  const nextFile = event.target.files?.[0] ?? null;
                  setResult(null);
                  setFile(nextFile);
                  setStatus(nextFile ? 'File loaded. Configure your output format.' : 'Select a file to begin.');
                  setProgress(0);
                }}
              />
            </label>

            <div className="meta-grid">
              <div>
                <span className="meta-label">Detected type</span>
                <strong>{file ? mediaType : '—'}</strong>
              </div>
              <div>
                <span className="meta-label">Source format</span>
                <strong>{file?.type || '—'}</strong>
              </div>
              <div>
                <span className="meta-label">File size</span>
                <strong>{file ? formatBytes(file.size) : '—'}</strong>
              </div>
            </div>

            <label className="field">
              <span>Target format</span>
              <select
                value={targetMime}
                onChange={(event) => setTargetMime(event.target.value)}
                disabled={!targetOptions.length || busy}
              >
                {targetOptions.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </select>
            </label>

            <label className="field">
              <span>Quality (image lossy formats)</span>
              <input
                type="range"
                min="0.1"
                max="1"
                step="0.05"
                value={quality}
                disabled={busy}
                onChange={(event) => setQuality(Number(event.target.value))}
              />
              <output>{quality.toFixed(2)}</output>
            </label>

            <details className="field">
              <summary>Advanced fallback options</summary>
              <label className="checkbox">
                <input
                  type="checkbox"
                  checked={enableWasmFallback}
                  onChange={(event) => setEnableWasmFallback(event.target.checked)}
                />
                Enable ffmpeg.wasm fallback
              </label>
              <label className="field">
                <span>Override fallback module URL</span>
                <input
                  type="url"
                  value={customModuleUrl}
                  onChange={(event) => setCustomModuleUrl(event.target.value)}
                  placeholder={defaultModuleUrl}
                />
                <small>Leave blank to use the bundled fallback module.</small>
              </label>
            </details>

            <button onClick={runConversion} disabled={!file || !targetMime || busy}>
              {busy ? 'Converting…' : 'Convert file'}
            </button>
          </div>

          <div className="card">
            <h2>What will happen</h2>
            <ul className="step-list">
              {steps.map((step) => (
                <li key={step.label} className={`step-item step-${step.state}`}>
                  <span className="step-dot" />
                  <span>{step.label}</span>
                </li>
              ))}
            </ul>
            <p className="muted">
              Route selected: <strong>{routeLabel(routeDecision)}</strong>
            </p>
          </div>
        </aside>

        <section className="panel stack">
          <div className="card">
            <h2>2. Conversion status</h2>
            <p>{status}</p>
            <progress value={progress} max={1} />
            <div className="progress-caption">
              <span>{Math.round(progress * 100)}%</span>
              <span>{busy ? 'Working in background-safe browser context' : 'Idle'}</span>
            </div>
          </div>

          <div className="card">
            <h2>3. Output preview</h2>
            {result && downloadUrl ? (
              <>
                <div className="preview-shell">{previewForResult(downloadUrl, result)}</div>
                <div className="meta-grid">
                  <div>
                    <span className="meta-label">Route</span>
                    <strong>{result.route}</strong>
                  </div>
                  <div>
                    <span className="meta-label">Output size</span>
                    <strong>{formatBytes(result.blob.size)}</strong>
                  </div>
                  <div>
                    <span className="meta-label">MIME type</span>
                    <strong>{result.blob.type || 'unknown'}</strong>
                  </div>
                </div>
                <a className="download-button" href={downloadUrl} download={result.outputName}>
                  Download {result.outputName}
                </a>
              </>
            ) : (
              <div className="empty-state">
                <strong>No output yet</strong>
                <p>
                  After conversion, the result will appear here so the user can immediately inspect
                  it before downloading.
                </p>
              </div>
            )}
          </div>

          <div className="card">
            <h2>Environment and support</h2>
            <p className="muted">
              This helps users understand why the app may choose the native browser route or the
              ffmpeg fallback.
            </p>
            <pre>{JSON.stringify(capabilities, null, 2)}</pre>
          </div>
        </section>
      </section>
    </main>
  );
}
