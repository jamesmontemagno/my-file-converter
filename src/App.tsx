import { useEffect, useMemo, useState, type ReactNode } from 'react';
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
import {
  buildOutputName,
  describeSelectedOptions,
  hasMediaTrim,
  stripExtension,
  type ConversionOptions,
} from './conversion-options';
import { convertWithWasmFallback } from './worker-client';

const APP_NAME = 'LocalMorph';

type Page = 'landing' | 'app' | 'privacy' | 'terms' | 'docs';
type StatusMode = 'idle' | 'ready' | 'working' | 'success' | 'error';
type ActivityTone = 'info' | 'success' | 'error';
type ActivityEntry = {
  id: number;
  message: string;
  progress: number;
  tone: ActivityTone;
  timestamp: string;
};

function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`;
}

function formatEventTime(date: Date) {
  return date.toLocaleTimeString([], {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });
}

function clampProgress(value: number) {
  return Math.max(0, Math.min(1, value));
}

function parsePositiveInteger(value: string) {
  if (!value.trim()) return null;
  const parsed = Number(value);
  if (!Number.isFinite(parsed) || parsed <= 0) return null;
  return Math.max(1, Math.round(parsed));
}

function parseNonNegativeNumber(value: string) {
  if (!value.trim()) return 0;
  const parsed = Number(value);
  if (!Number.isFinite(parsed) || parsed < 0) return 0;
  return parsed;
}

function getPageFromHash(): Page {
  if (window.location.hash === '#/app') return 'app';
  if (window.location.hash === '#/privacy') return 'privacy';
  if (window.location.hash === '#/terms') return 'terms';
  if (window.location.hash === '#/docs') return 'docs';
  return 'landing';
}

function titleForPage(page: Page) {
  if (page === 'app') return `${APP_NAME} | Browser File Converter`;
  if (page === 'privacy') return `${APP_NAME} | Privacy Policy`;
  if (page === 'terms') return `${APP_NAME} | Terms of Use`;
  if (page === 'docs') return `${APP_NAME} | Documentation`;
  return `${APP_NAME} | Convert Files Locally in Your Browser`;
}

function descriptionForPage(page: Page) {
  if (page === 'app')
    return 'Convert images, audio, and video files directly in your browser. No uploads, no servers — everything stays on your device.';
  if (page === 'privacy')
    return 'LocalMorph privacy policy. Learn how we handle your data — spoiler: we never receive your files because all conversion happens locally in your browser.';
  if (page === 'terms')
    return 'LocalMorph terms of use. Review the conditions under which you may use this browser-based file converter.';
  if (page === 'docs')
    return 'LocalMorph documentation. Learn about supported file formats, how browser-native conversion works, and when ffmpeg.wasm is used as a fallback.';
  return 'LocalMorph converts images, audio, and video files directly in your browser — no uploads, no servers, no privacy risks. Free, fast, and 100% client-side.';
}

function canonicalForPage(page: Page) {
  const base = 'https://localmorph.com/';
  if (page === 'app') return `${base}#/app`;
  if (page === 'privacy') return `${base}#/privacy`;
  if (page === 'terms') return `${base}#/terms`;
  if (page === 'docs') return `${base}#/docs`;
  return base;
}

function setMetaTag(name: string, content: string, property = false) {
  const attr = property ? 'property' : 'name';
  let tag = document.querySelector<HTMLMetaElement>(`meta[${attr}="${name}"]`);
  if (!tag) {
    tag = document.createElement('meta');
    tag.setAttribute(attr, name);
    document.head.appendChild(tag);
  }
  tag.setAttribute('content', content);
}

function setCanonicalTag(href: string) {
  let tag = document.querySelector<HTMLLinkElement>('link[rel="canonical"]');
  if (!tag) {
    tag = document.createElement('link');
    tag.setAttribute('rel', 'canonical');
    document.head.appendChild(tag);
  }
  tag.setAttribute('href', href);
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

function statusCopyFor(mode: StatusMode) {
  if (mode === 'working') {
    return {
      label: 'Converting now',
      detail: 'Live updates are streaming while the browser works.',
    };
  }

  if (mode === 'success') {
    return {
      label: 'Preview ready',
      detail: 'Conversion finished and the download is ready below.',
    };
  }

  if (mode === 'error') {
    return {
      label: 'Needs attention',
      detail: 'The latest conversion hit an error. Check the live log for details.',
    };
  }

  if (mode === 'ready') {
    return {
      label: 'Ready to convert',
      detail: 'Configuration is loaded and waiting for you to start.',
    };
  }

  return {
    label: 'Waiting for input',
    detail: 'Load a file and the converter will start reporting activity here.',
  };
}

function HamburgerMenu() {
  const [open, setOpen] = useState(false);
  return (
    <div className="hamburger-menu">
      <button
        className="hamburger-button ghost-button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        aria-label="Menu"
      >
        ☰
      </button>
      {open && (
        <>
          <div className="hamburger-overlay" onClick={() => setOpen(false)} />
          <nav className="hamburger-dropdown">
            <a href="#/docs" onClick={() => setOpen(false)}>
              Documentation
            </a>
            <a href="#/privacy" onClick={() => setOpen(false)}>
              Privacy
            </a>
            <a href="#/terms" onClick={() => setOpen(false)}>
              Terms
            </a>
          </nav>
        </>
      )}
    </div>
  );
}

function Footer() {
  return (
    <footer className="site-footer">
      <div className="footer-links">
        <a href="#">Home</a>
        <a href="#/app">Converter</a>
        <a href="#/docs">Documentation</a>
        <a href="#/privacy">Privacy Policy</a>
        <a href="#/terms">Terms of Use</a>
      </div>
      <p className="muted footer-note">
        {APP_NAME} is designed for browser-based local conversion. Review the legal pages before
        using it in production or for sensitive workflows.
      </p>
    </footer>
  );
}

function LandingPage({ onOpenApp }: { onOpenApp: () => void }) {
  return (
    <main className="page">
      <header className="topbar">
        <a className="brand brand-link" href="#">
          {APP_NAME}
        </a>
        <nav className="nav-actions">
          <button className="ghost-button" onClick={onOpenApp}>
            Open app
          </button>
          <HamburgerMenu />
        </nav>
      </header>

      <section className="hero">
        <div className="hero-copy">
          <span className="eyebrow">Private. Fast. Browser-native.</span>
          <h1>Convert video, audio, and images locally without uploading your files.</h1>
          <p className="hero-text">
            {APP_NAME} uses native browser APIs first and falls back to `ffmpeg.wasm` only when
            needed. The experience is designed so people always know what route is being used, what
            the browser supports, and what happens to their files.
          </p>
          <div className="hero-actions">
            <button onClick={onOpenApp}>Start converting</button>
            <a className="text-link" href="#features">
              Explore features
            </a>
            <a className="text-link" href="#/privacy">
              Privacy policy
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

      <Footer />
    </main>
  );
}

function LegalLayout({
  title,
  summary,
  eyebrow = 'Legal',
  children,
}: {
  title: string;
  summary: string;
  eyebrow?: string;
  children: ReactNode;
}) {
  return (
    <main className="page">
      <header className="topbar">
        <a className="brand brand-link" href="#">
          {APP_NAME}
        </a>
        <nav className="nav-actions">
          <a className="text-link" href="#/app">
            Open app
          </a>
          <HamburgerMenu />
        </nav>
      </header>

      <section className="legal-shell">
        <article className="card legal-card">
          <span className="eyebrow">{eyebrow}</span>
          <h1>{title}</h1>
          <p className="legal-summary">{summary}</p>
          {children}
        </article>
      </section>

      <Footer />
    </main>
  );
}

function PrivacyPage() {
  return (
    <LegalLayout
      title="Privacy Policy"
      summary={`${APP_NAME} is built for local, in-browser conversion. This policy explains what data stays on your device, what limited technical data may still be processed by hosting providers, and what to keep in mind when enabling optional external modules.`}
    >
      <section>
        <h2>1. Local file processing</h2>
        <p>
          Files selected in {APP_NAME} are intended to be processed in your browser on your device.
          The app does not require you to upload files to a managed application server to convert
          them.
        </p>
      </section>

      <section>
        <h2>2. Information the app uses while running</h2>
        <p>
          While you use the converter, the app may read file metadata that is available in the
          browser, such as the file name, size, and MIME type, so it can show route selection,
          progress, and output details.
        </p>
        <p>
          That information is used only within the running browser session unless your browser or an
          extension stores it separately.
        </p>
      </section>

      <section>
        <h2>3. Hosting and technical logs</h2>
        <p>
          If this site is hosted on a platform such as GitHub Pages, the hosting provider may
          collect standard technical logs like IP address, user agent, request time, and referrer as
          part of delivering the site. Those logs are outside the in-browser conversion flow and are
          governed by the hosting provider&apos;s own policies.
        </p>
      </section>

      <section>
        <h2>4. Optional external modules</h2>
        <p>
          {APP_NAME} lets you override the fallback module URL. If you choose to load a module from
          another host, your browser will request code from that third party.
        </p>
        <ul>
          <li>Your files are still intended to remain local in the browser workflow.</li>
          <li>You should only use module URLs from parties you trust.</li>
          <li>
            Third-party code may have its own privacy, security, and licensing implications, which
            you are responsible for reviewing.
          </li>
        </ul>
      </section>

      <section>
        <h2>5. Cookies and storage</h2>
        <p>
          {APP_NAME} does not require an account and does not intentionally rely on advertising
          cookies. The current app experience primarily uses in-memory session state while the page
          is open.
        </p>
      </section>

      <section>
        <h2>6. Your choices</h2>
        <p>
          You can stop using the app at any time by closing the page. You should avoid using the app
          with sensitive files unless you have reviewed the hosting setup, browser environment, and
          any optional third-party modules you enable.
        </p>
      </section>
    </LegalLayout>
  );
}

function TermsPage() {
  return (
    <LegalLayout
      title="Terms of Use"
      summary={`These terms govern access to ${APP_NAME}. They are meant to set expectations for acceptable use, output limitations, and liability for a browser-based file conversion tool.`}
    >
      <section>
        <h2>1. Use of the service</h2>
        <p>
          {APP_NAME} is provided as a browser-based file conversion tool. You may use it only in
          compliance with applicable law and only for content you have the right to process.
        </p>
      </section>

      <section>
        <h2>2. Your content and permissions</h2>
        <p>
          You are responsible for the files you choose to convert and for confirming that you have
          the rights, licenses, consents, and permissions needed to use and transform them.
        </p>
      </section>

      <section>
        <h2>3. Acceptable use</h2>
        <ul>
          <li>Do not use the app for unlawful, infringing, or abusive activity.</li>
          <li>Do not attempt to interfere with the site, hosting platform, or other users.</li>
          <li>
            Do not rely on the app for regulated, safety-critical, or guaranteed archival workflows.
          </li>
        </ul>
      </section>

      <section>
        <h2>4. Browser and output limitations</h2>
        <p>
          Conversion behavior depends on browser capabilities, codecs, file formats, memory limits,
          and optional fallback modules. Some conversions may fail, be lossy, or produce different
          results across browsers and devices.
        </p>
      </section>

      <section>
        <h2>5. No warranty</h2>
        <p>
          {APP_NAME} is provided on an &quot;as is&quot; and &quot;as available&quot; basis without warranties of any
          kind, whether express or implied, including warranties of merchantability, fitness for a
          particular purpose, non-infringement, availability, or accuracy of output.
        </p>
      </section>

      <section>
        <h2>6. Limitation of liability</h2>
        <p>
          To the maximum extent permitted by law, the app owner and contributors are not liable for
          any indirect, incidental, special, consequential, or punitive damages, or for any loss of
          data, profits, goodwill, or business interruption arising from your use of the app.
        </p>
      </section>

      <section>
        <h2>7. Changes</h2>
        <p>
          These terms may be updated as the product evolves. Continued use of the app after changes
          are published means you accept the revised terms.
        </p>
      </section>
    </LegalLayout>
  );
}

function DocsPage() {
  return (
    <LegalLayout
      eyebrow="Docs"
      title="Documentation"
      summary={`${APP_NAME} converts images, audio, and video files entirely in your browser. This page explains what formats are supported, how the conversion technology works, and what happens when a native browser route is not available.`}
    >
      <section>
        <h2>1. Supported formats</h2>
        <p>
          {APP_NAME} supports the following file types. Exact output availability depends on your
          browser and the codecs it has installed.
        </p>
        <h3>Images</h3>
        <ul>
          <li>
            <strong>PNG (.png)</strong> — lossless raster format; exported via the Canvas{' '}
            <code>toBlob</code> API in all modern browsers.
          </li>
          <li>
            <strong>JPEG (.jpg)</strong> — lossy raster format with adjustable quality; supported
            via Canvas in all modern browsers.
          </li>
          <li>
            <strong>WebP (.webp)</strong> — modern format with both lossy and lossless modes;
            supported in Chrome, Edge, Firefox, and recent Safari via Canvas.
          </li>
          <li>
            <strong>AVIF (.avif)</strong> — next-generation format with excellent compression;
            Canvas support varies by browser version.
          </li>
        </ul>
        <h3>Audio</h3>
        <ul>
          <li>
            <strong>WebM Opus (.webm)</strong> — open container with the Opus codec; supported
            natively in most browsers via the MediaRecorder API.
          </li>
          <li>
            <strong>Ogg Opus (.ogg)</strong> — alternative container for Opus audio; MediaRecorder
            support varies by browser.
          </li>
          <li>
            <strong>MP4 Audio (.m4a)</strong> — AAC audio in an MP4 container; typically requires
            the ffmpeg.wasm fallback.
          </li>
        </ul>
        <h3>Video</h3>
        <ul>
          <li>
            <strong>WebM VP8+Opus (.webm)</strong> — open video format; MediaRecorder support is
            broad across Chrome and Firefox.
          </li>
          <li>
            <strong>WebM VP9+Opus (.webm)</strong> — higher-efficiency WebM variant; support varies
            by browser.
          </li>
          <li>
            <strong>MP4 H.264+AAC (.mp4)</strong> — widely compatible format; uses the ffmpeg.wasm
            fallback path.
          </li>
        </ul>
      </section>

      <section>
        <h2>2. How conversion works</h2>
        <p>
          {APP_NAME} uses two conversion paths: a native browser route and an optional ffmpeg.wasm
          fallback. The app chooses the best path automatically based on your browser&apos;s
          capabilities and the selected format combination.
        </p>
        <h3>Native browser route</h3>
        <p>
          For images, the app draws your file onto an HTML canvas element and exports it in the
          target format using the browser&apos;s built-in <code>toBlob</code> API. This is fast,
          requires no additional downloads, and keeps all data on your device.
        </p>
        <p>
          For audio and video, the app decodes your source file using an HTML media element and
          pipes the stream into the MediaRecorder API, which re-encodes it in the selected format.
          This path works well for WebM output in Chrome and Firefox.
        </p>
        <h3>ffmpeg.wasm fallback</h3>
        <p>
          When the native route cannot handle a format combination — for example, producing MP4
          output or applying trim settings — the app loads ffmpeg compiled to WebAssembly and runs
          the conversion in a Web Worker. This keeps the main thread responsive and avoids uploading
          your file to a server.
        </p>
        <p>
          The ffmpeg.wasm module is loaded on demand. By default the bundled module URL is used, but
          you can override it in the advanced settings if you prefer to host your own copy.
        </p>
      </section>

      <section>
        <h2>3. Conversion route selection</h2>
        <p>
          The converter shows a route indicator before and during conversion so you always know
          which path is active:
        </p>
        <ul>
          <li>
            <strong>Native browser path</strong> — the selected format is supported directly by
            your browser with no additional modules required.
          </li>
          <li>
            <strong>ffmpeg.wasm fallback</strong> — the format requires the WebAssembly module,
            which will be loaded automatically if the fallback is enabled.
          </li>
          <li>
            <strong>Unsupported until fallback is enabled</strong> — the current format needs
            ffmpeg but the fallback is turned off in the advanced settings panel.
          </li>
        </ul>
        <p>
          Trim settings always force the ffmpeg route because the native MediaRecorder API does not
          support seeking before encoding.
        </p>
      </section>

      <section>
        <h2>4. Browser requirements</h2>
        <p>
          {APP_NAME} requires a modern browser released in the last two to three years. The
          following capabilities are used:
        </p>
        <ul>
          <li>
            <strong>Canvas API</strong> — required for all image conversions.
          </li>
          <li>
            <strong>MediaRecorder API</strong> — required for the native audio and video route.
          </li>
          <li>
            <strong>Web Workers</strong> — required for the ffmpeg.wasm fallback to run off the
            main thread.
          </li>
          <li>
            <strong>Blob and URL APIs</strong> — used for generating download links and previewing
            output in the browser.
          </li>
        </ul>
        <p>
          Chrome, Edge, and Firefox provide the broadest format coverage. Safari supports images and
          some audio formats natively but has more limited MediaRecorder codec support.
        </p>
      </section>

      <section>
        <h2>5. Privacy and data flow</h2>
        <p>
          Files are processed in memory inside your browser tab. They are not sent to any server as
          part of the conversion workflow. The only exception is if you configure a custom
          ffmpeg.wasm module URL that points to a third-party host — in that case your browser will
          request the module code from that host, though your files remain local.
        </p>
        <p>
          See the <a href="#/privacy">Privacy Policy</a> for the full data handling disclosure.
        </p>
      </section>
    </LegalLayout>
  );
}

export default function App() {
  const [page, setPage] = useState<Page>(getPageFromHash());
  const [file, setFile] = useState<File | null>(null);
  const [quality, setQuality] = useState(0.9);
  const [status, setStatus] = useState('Select a file to begin.');
  const [progress, setProgress] = useState(0);
  const [statusMode, setStatusMode] = useState<StatusMode>('idle');
  const [enableWasmFallback, setEnableWasmFallback] = useState(true);
  const [customModuleUrl, setCustomModuleUrl] = useState('');
  const [result, setResult] = useState<ConversionResult | null>(null);
  const [downloadUrl, setDownloadUrl] = useState('');
  const [capabilities, setCapabilities] = useState<CapabilityReport | null>(null);
  const [busy, setBusy] = useState(false);
  const [activityLog, setActivityLog] = useState<ActivityEntry[]>([]);
  const [logOpen, setLogOpen] = useState(true);

  const mediaType: MediaKind = classifyMediaType(file);
  const targetOptions = useMemo(() => targetFormatsFor(mediaType), [mediaType]);
  const [targetMime, setTargetMime] = useState('');
  const [outputBaseName, setOutputBaseName] = useState('');
  const [imageWidth, setImageWidth] = useState('');
  const [imageHeight, setImageHeight] = useState('');
  const [keepAspectRatio, setKeepAspectRatio] = useState(true);
  const [trimStart, setTrimStart] = useState('0');
  const [trimEnd, setTrimEnd] = useState('');

  useEffect(() => {
    const onHashChange = () => setPage(getPageFromHash());
    window.addEventListener('hashchange', onHashChange);
    return () => window.removeEventListener('hashchange', onHashChange);
  }, []);

  useEffect(() => {
    document.title = titleForPage(page);
    const description = descriptionForPage(page);
    const canonical = canonicalForPage(page);
    const title = titleForPage(page);

    setMetaTag('description', description);
    setMetaTag('og:title', title, true);
    setMetaTag('og:description', description, true);
    setMetaTag('og:url', canonical, true);
    setMetaTag('twitter:title', title);
    setMetaTag('twitter:description', description);
    setMetaTag('twitter:url', canonical);
    setCanonicalTag(canonical);
  }, [page]);

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
  const requestedOptions = useMemo<ConversionOptions>(
    () => ({
      outputBaseName: outputBaseName.trim(),
      image: {
        width: parsePositiveInteger(imageWidth),
        height: parsePositiveInteger(imageHeight),
        keepAspectRatio,
      },
      media: {
        trimStart: parseNonNegativeNumber(trimStart),
        trimEnd: parseNonNegativeNumber(trimEnd),
      },
    }),
    [imageHeight, imageWidth, keepAspectRatio, outputBaseName, trimEnd, trimStart],
  );
  const trimRequested =
    (mediaType === 'audio' || mediaType === 'video') && hasMediaTrim(requestedOptions.media);
  const trimValidationError =
    trimRequested &&
    requestedOptions.media.trimEnd > 0 &&
    requestedOptions.media.trimEnd <= requestedOptions.media.trimStart
      ? 'Trim end must be greater than trim start.'
      : '';
  const selectedOptions = useMemo(
    () => describeSelectedOptions(mediaType, requestedOptions),
    [mediaType, requestedOptions],
  );
  const selectedAdjustments = useMemo(
    () => selectedOptions.filter((entry) => !entry.startsWith('Save as ')),
    [selectedOptions],
  );
  const outputFileName = file && targetMime ? buildOutputName(file.name, targetMime, outputBaseName) : '—';
  const resultOutputName = result && file && targetMime ? buildOutputName(file.name, targetMime, outputBaseName) : result?.outputName ?? '';

  const routeDecision = useMemo(() => {
    if (!file || !targetMime) return 'blocked' as const;
    if (trimValidationError) return 'blocked' as const;
    if (trimRequested) return enableWasmFallback ? ('wasm' as const) : ('blocked' as const);
    if (supportsNativeRoute(file, targetMime)) return 'native' as const;
    if (enableWasmFallback) return 'wasm' as const;
    return 'blocked' as const;
  }, [enableWasmFallback, file, targetMime, trimRequested, trimValidationError]);
  const routeReason = useMemo(() => {
    if (!file || !targetMime) return 'Select a file and target format to see the conversion route.';
    if (trimValidationError) return trimValidationError;
    if (trimRequested) {
      return enableWasmFallback
        ? 'Trim settings require ffmpeg and will switch this job to the fallback route.'
        : 'Trim settings require ffmpeg. Enable the fallback route to continue.';
    }
    if (supportsNativeRoute(file, targetMime)) {
      return 'Current settings can stay on the native browser route.';
    }
    if (enableWasmFallback) {
      return 'This format combination is not supported natively, so ffmpeg fallback will be used.';
    }
    return 'This format combination needs ffmpeg fallback, but fallback is disabled.';
  }, [enableWasmFallback, file, targetMime, trimRequested, trimValidationError]);

  const steps = [
    { label: 'Choose file', state: file ? 'done' : 'current' },
    { label: 'Set options', state: file && targetMime ? 'done' : 'pending' },
    { label: 'Convert locally', state: busy ? 'current' : result ? 'done' : 'pending' },
    { label: 'Preview & download', state: result ? 'done' : 'pending' },
  ];
  const statusIndicator = useMemo(() => statusCopyFor(statusMode), [statusMode]);
  const activityEntries = useMemo(() => [...activityLog].reverse(), [activityLog]);

  function navigate(next: Page) {
    if (next === 'app') {
      window.location.hash = '/app';
    } else if (next === 'privacy') {
      window.location.hash = '/privacy';
    } else if (next === 'terms') {
      window.location.hash = '/terms';
    } else if (next === 'docs') {
      window.location.hash = '/docs';
    } else {
      window.location.hash = '';
    }

    setPage(next);
  }

  function handleProgress(nextProgress: number, message: string, tone: ActivityTone = 'info') {
    const normalizedProgress = clampProgress(nextProgress);
    setStatus(message);
    setProgress(normalizedProgress);
    setActivityLog((previous) => {
      const lastEntry = previous[previous.length - 1];
      const timestamp = formatEventTime(new Date());

      if (tone === 'info' && lastEntry && lastEntry.message === message && lastEntry.tone === tone) {
        return [
          ...previous.slice(0, -1),
          {
            ...lastEntry,
            progress: normalizedProgress,
            timestamp,
          },
        ];
      }

      const nextEntry: ActivityEntry = {
        id: (lastEntry?.id ?? 0) + 1,
        message,
        progress: normalizedProgress,
        tone,
        timestamp,
      };
      return [...previous.slice(-24), nextEntry];
    });
  }

  function markConfigurationChanged(message: string) {
    setResult(null);
    setProgress(0);
    setStatus(message);
    setStatusMode(file ? 'ready' : 'idle');
  }

  async function runConversion() {
    if (!file || !targetMime || trimValidationError) return;

    setBusy(true);
    setStatusMode('working');
    setLogOpen(true);
    setResult(null);
    setActivityLog((previous) => {
      const lastEntry = previous[previous.length - 1];
      const nextEntries: ActivityEntry[] = [
        ...previous.slice(-24),
        {
          id: (lastEntry?.id ?? 0) + 1,
          message: `Starting ${routeLabel(routeDecision).toLowerCase()} conversion`,
          progress: 0,
          tone: 'info',
          timestamp: formatEventTime(new Date()),
        },
      ];
      const optionsLabel = selectedAdjustments.join(' • ');
      if (optionsLabel) {
        nextEntries.push({
          id: (lastEntry?.id ?? 0) + 2,
          message: optionsLabel,
          progress: 0,
          tone: 'info',
          timestamp: formatEventTime(new Date()),
        });
      }
      return nextEntries;
    });
    handleProgress(0.05, 'Preparing conversion job');

    try {
      let next: ConversionResult;

      if (routeDecision === 'native') {
        next =
          mediaType === 'image'
            ? await convertImage({
                file,
                targetMime,
                quality,
                imageOptions: requestedOptions.image,
                onProgress: handleProgress,
              })
            : await convertViaMediaRecorder({ file, targetMime, onProgress: handleProgress });
      } else if (routeDecision === 'wasm' && enableWasmFallback) {
        next = await convertWithWasmFallback({
          file,
          targetMime,
          wasmModuleUrl: moduleUrl,
          options: requestedOptions,
          onProgress: handleProgress,
        });
      } else {
        throw new Error('Native route is unsupported and ffmpeg fallback is disabled.');
      }

      setResult({
        ...next,
        outputName: buildOutputName(file.name, targetMime, requestedOptions.outputBaseName),
      });
      setStatusMode('success');
      handleProgress(1, 'Conversion complete — preview ready below', 'success');
      setLogOpen(false);
    } catch (error) {
      setStatusMode('error');
      handleProgress(
        0,
        `Conversion failed: ${error instanceof Error ? error.message : 'Unknown error'}`,
        'error',
      );
    } finally {
      setBusy(false);
    }
  }

  if (page === 'landing') {
    return <LandingPage onOpenApp={() => navigate('app')} />;
  }

  if (page === 'privacy') {
    return <PrivacyPage />;
  }

  if (page === 'terms') {
    return <TermsPage />;
  }

  if (page === 'docs') {
    return <DocsPage />;
  }

  return (
    <main className="page">
      <header className="topbar">
        <a className="brand brand-link" href="#">
          {APP_NAME}
        </a>
        <HamburgerMenu />
      </header>

      <section className="workspace-hero">
        <div>
          <span className="eyebrow">Converter workspace</span>
          <h1>Convert files locally.</h1>
          <p className="hero-text compact">Choose a file, pick an output, and convert.</p>
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
                  setProgress(0);
                  setOutputBaseName(nextFile ? stripExtension(nextFile.name) : '');
                  setImageWidth('');
                  setImageHeight('');
                  setKeepAspectRatio(true);
                  setTrimStart('0');
                  setTrimEnd('');
                  setStatus(nextFile ? 'File loaded. Configure your output format.' : 'Select a file to begin.');
                  setStatusMode(nextFile ? 'ready' : 'idle');
                  setActivityLog(
                    nextFile
                      ? [
                          {
                            id: 1,
                            message: `Loaded ${nextFile.name} (${formatBytes(nextFile.size)})`,
                            progress: 0,
                            tone: 'info',
                            timestamp: formatEventTime(new Date()),
                          },
                          {
                            id: 2,
                            message: 'Configure your output format and start conversion when ready.',
                            progress: 0,
                            tone: 'info',
                            timestamp: formatEventTime(new Date()),
                          },
                        ]
                      : [],
                  );
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
                onChange={(event) => {
                  const nextTargetMime = event.target.value;
                  setTargetMime(nextTargetMime);
                  if (file) {
                    const nextTarget = targetOptions.find((option) => option.value === nextTargetMime);
                    markConfigurationChanged(
                      `Target format set to ${nextTarget?.label ?? nextTargetMime}. Ready to convert.`,
                    );
                  }
                }}
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
              <span>Output file name</span>
              <input
                type="text"
                value={outputBaseName}
                disabled={!file || busy}
                onChange={(event) => setOutputBaseName(event.target.value)}
                placeholder={file ? stripExtension(file.name) : 'converted-file'}
              />
              <small>Extension is added automatically from the selected output format.</small>
            </label>

            {mediaType === 'image' ? (
              <div className="option-section">
                <h3>Image adjustments</h3>
                <div className="option-grid">
                  <label className="field">
                    <span>Width (px)</span>
                    <input
                      type="number"
                      min="1"
                      inputMode="numeric"
                      value={imageWidth}
                      disabled={busy}
                      onChange={(event) => {
                        setImageWidth(event.target.value);
                        if (file) {
                          markConfigurationChanged('Image size updated. Ready to convert.');
                        }
                      }}
                      placeholder="Original width"
                    />
                  </label>
                  <label className="field">
                    <span>Height (px)</span>
                    <input
                      type="number"
                      min="1"
                      inputMode="numeric"
                      value={imageHeight}
                      disabled={busy}
                      onChange={(event) => {
                        setImageHeight(event.target.value);
                        if (file) {
                          markConfigurationChanged('Image size updated. Ready to convert.');
                        }
                      }}
                      placeholder="Original height"
                    />
                  </label>
                </div>
                <label className="checkbox">
                  <input
                    type="checkbox"
                    checked={keepAspectRatio}
                    disabled={busy}
                    onChange={(event) => {
                      setKeepAspectRatio(event.target.checked);
                      if (file) {
                        markConfigurationChanged('Aspect ratio preference updated. Ready to convert.');
                      }
                    }}
                  />
                  Keep aspect ratio
                </label>
                <small>Leave width or height blank to keep the original dimension.</small>
                <label className="field">
                  <span>Quality (image lossy formats)</span>
                  <input
                    type="range"
                    min="0.1"
                    max="1"
                    step="0.05"
                    value={quality}
                    disabled={busy}
                    onChange={(event) => {
                      setQuality(Number(event.target.value));
                      if (file) {
                        markConfigurationChanged('Image quality updated. Ready to convert.');
                      }
                    }}
                  />
                  <output>{quality.toFixed(2)}</output>
                </label>
              </div>
            ) : null}

            {mediaType === 'audio' || mediaType === 'video' ? (
              <div className="option-section">
                <h3>Trim clip</h3>
                <div className="option-grid">
                  <label className="field">
                    <span>Start time (seconds)</span>
                    <input
                      type="number"
                      min="0"
                      step="0.1"
                      inputMode="decimal"
                      value={trimStart}
                      disabled={busy}
                      onChange={(event) => {
                        setTrimStart(event.target.value);
                        if (file) {
                          markConfigurationChanged('Trim settings updated. Ready to convert.');
                        }
                      }}
                    />
                  </label>
                  <label className="field">
                    <span>End time (seconds)</span>
                    <input
                      type="number"
                      min="0"
                      step="0.1"
                      inputMode="decimal"
                      value={trimEnd}
                      disabled={busy}
                      onChange={(event) => {
                        setTrimEnd(event.target.value);
                        if (file) {
                          markConfigurationChanged('Trim settings updated. Ready to convert.');
                        }
                      }}
                      placeholder="Leave blank for full length"
                    />
                  </label>
                </div>
                <small>
                  Trim controls use ffmpeg when the native browser route cannot apply them.
                </small>
                {trimValidationError ? <p className="form-error">{trimValidationError}</p> : null}
              </div>
            ) : null}

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

            <div className="selection-summary">
              <span className="meta-label">Output file</span>
              <strong>{outputFileName}</strong>
              {selectedAdjustments.length ? (
                <ul className="option-summary-list">
                  {selectedAdjustments.map((entry) => (
                    <li key={entry}>{entry}</li>
                  ))}
                </ul>
              ) : (
                <p className="muted">No extra adjustments selected yet.</p>
              )}
            </div>

            <button onClick={runConversion} disabled={!file || !targetMime || busy || Boolean(trimValidationError)}>
              {busy ? 'Converting…' : 'Convert file'}
            </button>
          </div>

          <div className="card">
            <h2>What happens next</h2>
            <ul className="step-list">
              {steps.map((step) => (
                <li key={step.label} className={`step-item step-${step.state}`}>
                  <span className="step-dot" />
                  <span>{step.label}</span>
                </li>
              ))}
            </ul>
            <p className="muted">
              Your file stays in the browser during conversion unless you choose a third-party
              fallback module URL.
            </p>
            <p className="muted">
              Route selected: <strong>{routeLabel(routeDecision)}</strong>
            </p>
            <p className="muted">{routeReason}</p>
          </div>
        </aside>

        <section className="panel stack">
          <div className="card">
            <div className="status-header">
              <div>
                <h2>2. Conversion status</h2>
                <p>{status}</p>
              </div>
              <div className={`status-indicator status-${statusMode}`} aria-live="polite">
                <span className="status-dot" />
                <div>
                  <strong>{statusIndicator.label}</strong>
                  <small>{statusIndicator.detail}</small>
                </div>
              </div>
            </div>
            <progress value={progress} max={1} />
            <div className="progress-caption">
              <span>{Math.round(progress * 100)}%</span>
              <span>{busy ? 'Streaming live updates below' : statusIndicator.detail}</span>
            </div>
            <button
              type="button"
              className="log-toggle"
              onClick={() => setLogOpen((open) => !open)}
              aria-expanded={logOpen}
            >
              {logOpen ? 'Hide live activity' : 'Show live activity'}
            </button>
            {logOpen ? (
              <div className="activity-log" role="log" aria-live="polite" aria-relevant="additions text">
                {activityEntries.length ? (
                  activityEntries.map((entry) => (
                    <article key={entry.id} className={`activity-entry activity-${entry.tone}`}>
                      <div className="activity-entry-header">
                        <strong>{entry.message}</strong>
                        <span>{entry.timestamp}</span>
                      </div>
                      <div className="activity-entry-meta">
                        <span>{Math.round(entry.progress * 100)}%</span>
                        <span>
                          {entry.tone === 'error'
                            ? 'Needs attention'
                            : entry.tone === 'success'
                              ? 'Completed'
                              : entry.progress === 0
                                ? 'Ready'
                                : 'Updated'}
                        </span>
                      </div>
                    </article>
                  ))
                ) : (
                  <div className="activity-empty">
                    We&apos;ll stream conversion updates here as soon as a file is loaded and a job
                    begins.
                  </div>
                )}
              </div>
            ) : null}
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
                <a className="download-button" href={downloadUrl} download={resultOutputName}>
                  Download {resultOutputName}
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

          <details className="card info-details">
            <summary>Technical support details</summary>
            <p className="muted">
              Browser capability details are available here if you need to understand route
              selection or troubleshoot a conversion.
            </p>
            <pre>{JSON.stringify(capabilities, null, 2)}</pre>
          </details>
        </section>
      </section>

      <Footer />
    </main>
  );
}
