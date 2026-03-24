import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useRegisterSW } from 'virtual:pwa-register/react';
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
  isConversionAbortError,
  supportsNativeRoute,
  type ConversionActivity,
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
import { LogoIcon } from './Logo';
import {
  ConvertingStep,
  ResultsStep,
  SettingsStep,
  UploadStep,
  WizardStepper,
} from './components/converter';

const APP_NAME = 'LocalMorph';
const MAX_ACTIVITY_HISTORY_LENGTH = 160;
const MAX_RAW_OUTPUT_ENTRIES = 80;

type Page = 'landing' | 'app' | 'privacy' | 'terms' | 'docs';
type StatusMode = 'idle' | 'ready' | 'working' | 'success' | 'error' | 'canceled';
type ActivityTone = 'info' | 'success' | 'error';
type ActivityVariant = 'milestone' | 'raw';
type RouteDecision = 'native' | 'wasm' | 'blocked';
type RoutePreference = 'auto' | 'native' | 'ffmpeg';
type ConverterStep = 'upload' | 'settings' | 'converting' | 'results';
type StepState = 'done' | 'current' | 'pending';
type ActivityEntry = {
  id: number;
  message: string;
  detail?: string;
  progress: number;
  tone: ActivityTone;
  timestamp: string;
  variant: ActivityVariant;
  source?: 'native' | 'ffmpeg';
};
type ResolvedRoute = {
  decision: RouteDecision;
  reason: string;
  source?: 'native' | 'ffmpeg';
};
type SizeChangeSummary = {
  trend: 'smaller' | 'larger' | 'same';
  deltaLabel: string;
  summaryLabel: string;
};

type WizardStep = {
  id: ConverterStep;
  label: string;
  state: StepState;
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

function formatPercentage(value: number) {
  const digits = value < 10 ? 1 : 0;
  return `${value.toFixed(digits)}%`;
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

function hasStandaloneNavigatorFlag(nav: Navigator): nav is Navigator & { standalone?: boolean } {
  return 'standalone' in nav;
}

function isStandaloneLaunch() {
  return (
    window.matchMedia('(display-mode: standalone)').matches ||
    window.matchMedia('(display-mode: window-controls-overlay)').matches ||
    (hasStandaloneNavigatorFlag(navigator) && navigator.standalone === true)
  );
}

function getInitialPage(): Page {
  if (!window.location.hash && isStandaloneLaunch()) {
    window.location.hash = '#/app';
    return 'app';
  }

  return getPageFromHash();
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
    return 'LocalMorph documentation. Learn about supported file formats, how browser-native conversion works, and when to use the ffmpeg.wasm route.';
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

function routePreferenceLabel(preference: RoutePreference) {
  if (preference === 'native') return 'Prefer browser-native';
  if (preference === 'ffmpeg') return 'Force ffmpeg.wasm';
  return 'Auto';
}

function routeLabel(route: RouteDecision, preference: RoutePreference) {
  if (route === 'native') return 'Native browser path';
  if (route === 'wasm') {
    return preference === 'ffmpeg' ? 'Forced ffmpeg.wasm route' : 'ffmpeg.wasm fallback';
  }
  return preference === 'ffmpeg'
    ? 'Blocked until ffmpeg.wasm is enabled'
    : 'Unsupported until fallback is enabled';
}

function sourceForRouteDecision(route: RouteDecision) {
  if (route === 'native') return 'native' as const;
  if (route === 'wasm') return 'ffmpeg' as const;
  return undefined;
}

function isNativeConversionRoute(route: string) {
  return route === 'native-image' || route === 'native-mediarecorder';
}

function resolveRoute(args: {
  enableWasmFallback: boolean;
  file: File | null;
  routePreference: RoutePreference;
  targetMime: string;
  trimRequested: boolean;
  trimValidationError: string;
}): ResolvedRoute {
  const { enableWasmFallback, file, routePreference, targetMime, trimRequested, trimValidationError } = args;

  if (!file || !targetMime) {
    return {
      decision: 'blocked',
      reason: 'Select a file and target format to see the conversion route.',
    };
  }

  if (trimValidationError) {
    return {
      decision: 'blocked',
      reason: trimValidationError,
    };
  }

  if (routePreference === 'ffmpeg') {
    return enableWasmFallback
      ? {
          decision: 'wasm',
          reason: trimRequested
            ? 'Force ffmpeg.wasm is enabled, so trim settings and conversion will stay on the WebAssembly route.'
            : 'Force ffmpeg.wasm is enabled, so this job will skip browser-native encoding and use the WebAssembly route.',
          source: 'ffmpeg',
        }
      : {
          decision: 'blocked',
          reason: 'Force ffmpeg.wasm is selected, but the fallback route is disabled. Enable it to continue.',
        };
  }

  if (trimRequested) {
    return enableWasmFallback
      ? {
          decision: 'wasm',
          reason:
            routePreference === 'native'
              ? 'Browser-native routing was preferred, but trim settings require ffmpeg.wasm for this conversion.'
              : 'Trim settings require ffmpeg and will switch this job to the fallback route.',
          source: 'ffmpeg',
        }
      : {
          decision: 'blocked',
          reason:
            routePreference === 'native'
              ? 'Browser-native routing was preferred, but trim settings still require ffmpeg. Enable the fallback route to continue.'
              : 'Trim settings require ffmpeg. Enable the fallback route to continue.',
        };
  }

  if (supportsNativeRoute(file, targetMime)) {
    return {
      decision: 'native',
      reason:
        routePreference === 'native'
          ? 'Browser-native routing is preferred and supported for this format combination.'
          : 'Current settings can stay on the native browser route.',
      source: 'native',
    };
  }

  if (enableWasmFallback) {
    return {
      decision: 'wasm',
      reason:
        routePreference === 'native'
          ? 'Browser-native routing was preferred, but this format combination needs ffmpeg.wasm instead.'
          : 'This format combination is not supported natively, so ffmpeg fallback will be used.',
      source: 'ffmpeg',
    };
  }

  return {
    decision: 'blocked',
    reason:
      routePreference === 'native'
        ? 'This format combination cannot stay on the browser-native route, and ffmpeg fallback is disabled.'
        : 'This format combination needs ffmpeg fallback, but fallback is disabled.',
  };
}

function describeSizeChange(inputBytes: number, outputBytes: number): SizeChangeSummary {
  const delta = outputBytes - inputBytes;
  const absoluteDelta = Math.abs(delta);

  if (absoluteDelta === 0) {
    return {
      trend: 'same',
      deltaLabel: '0 B',
      summaryLabel: 'No size change',
    };
  }

  const percentage = inputBytes > 0 ? (absoluteDelta / inputBytes) * 100 : 0;
  const direction = delta < 0 ? 'smaller' : 'larger';
  const signedDelta = `${delta < 0 ? '-' : '+'}${formatBytes(absoluteDelta)}`;

  return {
    trend: direction,
    deltaLabel: signedDelta,
    summaryLabel: `${formatPercentage(percentage)} ${direction} (${signedDelta})`,
  };
}

function sizeChangeGuidance(args: {
  inputFile: File;
  outputMime: string;
  result: ConversionResult;
}) {
  const change = describeSizeChange(args.inputFile.size, args.result.blob.size);

  if (change.trend === 'same') {
    return 'The converted file is effectively the same size as the original.';
  }

  if (change.trend === 'smaller') {
    return 'The converted file is smaller than the source and ready to download.';
  }

  const nativeRoute = isNativeConversionRoute(args.result.route);
  const webMediaOutput = args.outputMime.includes('webm') || args.outputMime.includes('webp');

  if (nativeRoute && webMediaOutput) {
    return 'Browser-native WebM/WebP encoders can increase file size for some sources. Force ffmpeg.wasm if you want a more predictable encoder path.';
  }

  if (args.result.route === 'wasm-ffmpeg') {
    return 'This ffmpeg.wasm output ended up larger than the source. More encoder tuning may help in a future update.';
  }

  return 'This output is larger than the source. Try a different route or format if file size matters more than speed.';
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

  if (mode === 'canceled') {
    return {
      label: 'Canceled',
      detail: 'The active conversion was stopped before the output was produced.',
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

function sourceLabel(source?: 'native' | 'ffmpeg') {
  if (source === 'ffmpeg') return 'ffmpeg output';
  if (source === 'native') return 'Browser pipeline';
  return '';
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
      <div className="footer-nav">
        <div className="footer-links">
          <span className="footer-heading">Explore</span>
          <a href="#">Home</a>
          <a href="#/app">Converter</a>
          <a href="#/docs">Documentation</a>
        </div>
        <div className="footer-links">
          <span className="footer-heading">Legal</span>
          <a href="#/privacy">Privacy Policy</a>
          <a href="#/terms">Terms of Use</a>
        </div>
      </div>
      <div className="footer-meta">
        <p className="muted footer-note">
          {APP_NAME} is designed for browser-based local conversion. Review the legal pages before
          using it in production or for sensitive workflows.
        </p>
        <a
          className="footer-project-link"
          href="https://github.com/jamesmontemagno/my-file-converter"
          target="_blank"
          rel="noreferrer"
        >
          GitHub project
        </a>
      </div>
    </footer>
  );
}

function UpdateNotification() {
  const {
    needRefresh: [needRefresh, setNeedRefresh],
    offlineReady: [offlineReady, setOfflineReady],
    updateServiceWorker,
  } = useRegisterSW();

  function close() {
    setOfflineReady(false);
    setNeedRefresh(false);
  }

  if (!offlineReady && !needRefresh) return null;

  return (
    <div className="pwa-toast" role="alert">
      <div className="pwa-toast-body">
        {offlineReady ? (
          <span>{APP_NAME} is ready to work offline.</span>
        ) : (
          <span>A new version of {APP_NAME} is available.</span>
        )}
      </div>
      <div className="pwa-toast-actions">
        {needRefresh && (
          <button className="ghost-button pwa-toast-btn" onClick={() => updateServiceWorker(true)}>
            Update
          </button>
        )}
        <button className="ghost-button pwa-toast-btn" onClick={close}>
          Dismiss
        </button>
      </div>
    </div>
  );
}

const INSTALL_STEPS: { platform: string; steps: string[] }[] = [
  {
    platform: 'Chrome / Edge (desktop)',
    steps: [
      'Look for the install icon (⊕) in the browser address bar.',
      'Click it and choose "Install".',
      'The app opens in its own window without browser chrome.',
    ],
  },
  {
    platform: 'Chrome (Android)',
    steps: [
      'Tap the three-dot menu in the top-right corner.',
      'Select "Add to Home screen".',
      'Tap "Add" to confirm.',
    ],
  },
  {
    platform: 'Safari (iPhone / iPad)',
    steps: [
      'Tap the Share button at the bottom of Safari.',
      'Scroll down and tap "Add to Home Screen".',
      'Tap "Add" in the top-right corner.',
    ],
  },
  {
    platform: 'Firefox (desktop)',
    steps: [
      'Install the "Progressive Web Apps for Firefox" extension.',
      'Click the PWA icon that appears in the address bar.',
      'Click "Install" to pin the app.',
    ],
  },
];

function FeaturesSection({ onOpenApp }: { onOpenApp: () => void }) {
  return (
    <section className="landing-section">
      <div className="section-header">
        <span className="eyebrow">How it works</span>
        <h2>Convert files in four clear steps</h2>
        <p className="hero-text">
          The home screen already covers the benefits, so this section focuses on the flow people
          follow from upload to download.
        </p>
      </div>
      <div className="workflow-steps">
        <div>
          <span>1</span>
          <h3>Choose a file</h3>
          <p>Upload a video, audio clip, or image and let the app detect the best options.</p>
        </div>
        <div>
          <span>2</span>
          <h3>Pick the output</h3>
          <p>Select the format that makes sense and see whether the browser can handle it natively.</p>
        </div>
        <div>
          <span>3</span>
          <h3>Convert with visibility</h3>
          <p>Follow clear progress updates so you always know what route is running.</p>
        </div>
        <div>
          <span>4</span>
          <h3>Preview and download</h3>
          <p>Check the converted result right in the app before saving it to your device.</p>
        </div>
      </div>
      <section className="cta-band">
        <h2>Ready to try the full converter experience?</h2>
        <button onClick={onOpenApp}>Go to the app workspace</button>
      </section>
    </section>
  );
}

function InstallSection() {
  const [activeTab, setActiveTab] = useState(0);
  const { platform, steps } = INSTALL_STEPS[activeTab] ?? INSTALL_STEPS[0];

  return (
    <section className="landing-section install-section">
      <div className="section-header">
        <span className="eyebrow">Available as an app</span>
        <h2>Install {APP_NAME} on your device</h2>
        <p className="hero-text">
          Pin {APP_NAME} to your home screen or desktop so it loads instantly, works offline, and
          feels like a native app — with no app store required.
        </p>
      </div>
      <div className="tab-bar" role="tablist">
        {INSTALL_STEPS.map(({ platform: p }, i) => (
          <button
            key={p}
            id={`tab-install-${i}`}
            role="tab"
            aria-selected={activeTab === i}
            aria-controls={`tabpanel-install-${i}`}
            className={`tab-btn${activeTab === i ? ' tab-btn--active' : ''}`}
            onClick={() => setActiveTab(i)}
          >
            {p}
          </button>
        ))}
      </div>
      <div
        id={`tabpanel-install-${activeTab}`}
        role="tabpanel"
        aria-labelledby={`tab-install-${activeTab}`}
        className="install-tabpanel"
      >
        <h3>{platform}</h3>
        <ol className="install-steps-list">
          {steps.map((step) => (
            <li key={step}>{step}</li>
          ))}
        </ol>
      </div>
    </section>
  );
}

function LandingPage({ onOpenApp }: { onOpenApp: () => void }) {
  return (
    <main className="page">
      <header className="topbar">
        <a className="brand brand-link" href="#">
          <LogoIcon size={28} />
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
            {APP_NAME} uses native browser APIs first, can fall back to `ffmpeg.wasm` when needed,
            and also lets you force the ffmpeg route when you want a more predictable encoder path.
            The experience is designed so people always know what route is being used, what the
            browser supports, and what happens to their files.
          </p>
          <div className="hero-actions">
            <button onClick={onOpenApp}>Start converting</button>
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
            <p>Automatic or forced ffmpeg path for broader format coverage.</p>
          </div>
          <div className="hero-stat">
            <strong>Clear status and output</strong>
            <p>Progress, route visibility, and visual output preview built in.</p>
          </div>
        </div>
      </section>

      <FeaturesSection onOpenApp={onOpenApp} />

      <InstallSection />

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
          <LogoIcon size={28} />
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
        summary={`${APP_NAME} converts images, audio, and video files entirely in your browser. This page explains what formats are supported, how the conversion technology works, and when you may want to choose the ffmpeg.wasm route yourself.`}
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
          route. By default the app chooses the best path automatically based on your browser&apos;s
          capabilities and the selected format combination, but the advanced settings can also force
          the ffmpeg route when you want it.
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
          the conversion in a Web Worker. You can also force this route manually if you want to
          compare output size or avoid browser-native encoder behavior. This keeps the main thread
          responsive and avoids uploading your file to a server.
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
          which path is active. By default it auto-picks the fastest compatible route, but advanced
          settings also let you prefer browser-native output or force ffmpeg.wasm:
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
            <strong>Forced ffmpeg.wasm route</strong> — the WebAssembly module is selected
            intentionally, even when the browser-native route is available.
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
  const [page, setPage] = useState<Page>(() => getInitialPage());
  const [activeStep, setActiveStep] = useState<ConverterStep>('upload');
  const [file, setFile] = useState<File | null>(null);
  const [quality, setQuality] = useState(0.9);
  const [status, setStatus] = useState('Select a file to begin.');
  const [statusDetail, setStatusDetail] = useState('Load a file and the converter will start reporting activity here.');
  const [progress, setProgress] = useState(0);
  const [statusMode, setStatusMode] = useState<StatusMode>('idle');
  const [enableWasmFallback, setEnableWasmFallback] = useState(true);
  const [routePreference, setRoutePreference] = useState<RoutePreference>('auto');
  const [customModuleUrl, setCustomModuleUrl] = useState('');
  const [result, setResult] = useState<ConversionResult | null>(null);
  const [downloadUrl, setDownloadUrl] = useState('');
  const [capabilities, setCapabilities] = useState<CapabilityReport | null>(null);
  const [busy, setBusy] = useState(false);
  const [cancelRequested, setCancelRequested] = useState(false);
  const [activityLog, setActivityLog] = useState<ActivityEntry[]>([]);
  const [logOpen, setLogOpen] = useState(false);
  const [statusSource, setStatusSource] = useState<'native' | 'ffmpeg' | undefined>();
  const [lastActivityAt, setLastActivityAt] = useState(() => Date.now());
  const progressRef = useRef(0);
  const activeAbortController = useRef<AbortController | null>(null);

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

  useEffect(() => {
    progressRef.current = progress;
  }, [progress]);

  useEffect(() => {
    if (!busy || statusMode !== 'working' || statusSource !== 'ffmpeg') return;

    const interval = window.setInterval(() => {
      const silenceMs = Date.now() - lastActivityAt;
      if (silenceMs < 10_000) return;
      const silenceSec = Math.round(silenceMs / 1000);
      setStatusDetail(
        `ffmpeg is still encoding (no output for ${silenceSec}s). ` +
        'Single-thread WebAssembly encoding is much slower than native FFmpeg — ' +
        'the encoder is working but progress events only fire when full frames complete.',
      );
    }, 4000);

    return () => window.clearInterval(interval);
  }, [busy, lastActivityAt, statusMode, statusSource]);

  const defaultModuleUrl = 'Bundled ffmpeg fallback module';
  const moduleUrl = customModuleUrl.trim();
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

  const resolvedRoute = useMemo(
    () =>
      resolveRoute({
        enableWasmFallback,
        file,
        routePreference,
        targetMime,
        trimRequested,
        trimValidationError,
      }),
    [enableWasmFallback, file, routePreference, targetMime, trimRequested, trimValidationError],
  );
  const routeDecision = resolvedRoute.decision;
  const routeReason = resolvedRoute.reason;
  const routeSource = resolvedRoute.source;
  const routeDisplayLabel = routeLabel(routeDecision, routePreference);

  const canConvert = Boolean(file && targetMime && !trimValidationError && routeDecision !== 'blocked');
  const stepOrder: ConverterStep[] = ['upload', 'settings', 'converting', 'results'];
  const activeStepIndex = stepOrder.indexOf(activeStep);
  const wizardSteps: WizardStep[] = stepOrder.map((step, index) => ({
    id: step,
    label:
      step === 'upload'
        ? 'Choose file'
        : step === 'settings'
          ? 'Set options'
          : step === 'converting'
            ? 'Convert locally'
            : 'Preview & download',
    state: index < activeStepIndex ? 'done' : index === activeStepIndex ? 'current' : 'pending',
  }));
  const statusIndicator = useMemo(() => statusCopyFor(statusMode), [statusMode]);
  const recentMilestones = useMemo(
    () =>
      [...activityLog]
        .filter((entry) => entry.variant === 'milestone')
        .slice(-3)
        .reverse(),
    [activityLog],
  );
  const rawOutputEntries = useMemo(
    () => activityLog.filter((entry) => entry.variant === 'raw').slice(-MAX_RAW_OUTPUT_ENTRIES),
    [activityLog],
  );
  const liveStatusDetail = statusDetail || statusIndicator.detail;
  const sizeChange = useMemo(
    () => (file && result ? describeSizeChange(file.size, result.blob.size) : null),
    [file, result],
  );
  const sizeGuidance = useMemo(
    () =>
      file && result
        ? sizeChangeGuidance({
            inputFile: file,
            outputMime: result.blob.type || targetMime,
            result,
          })
        : '',
    [file, result, targetMime],
  );

  function initializeForFile(nextFile: File | null) {
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
    setStatusDetail(
      nextFile
        ? 'Choose an output format to see the live route and progress details.'
        : 'Load a file and the converter will start reporting activity here.',
    );
    setStatusMode(nextFile ? 'ready' : 'idle');
    setStatusSource(undefined);
    setCancelRequested(false);
    setLogOpen(false);
    setLastActivityAt(Date.now());
    setActivityLog(
      nextFile
        ? [
            {
              id: 1,
              message: `Loaded ${nextFile.name} (${formatBytes(nextFile.size)})`,
              detail: 'File metadata is ready and the browser can configure the conversion route.',
              progress: 0,
              tone: 'info',
              timestamp: formatEventTime(new Date()),
              variant: 'milestone',
            },
            {
              id: 2,
              message: 'Configure your output format and start conversion when ready.',
              detail: 'The live progress view will update as soon as the conversion begins.',
              progress: 0,
              tone: 'info',
              timestamp: formatEventTime(new Date()),
              variant: 'milestone',
            },
          ]
        : [],
    );
    setActiveStep(nextFile ? 'settings' : 'upload');
  }

  function restartConverter() {
    if (busy) return;
    setQuality(0.9);
    setEnableWasmFallback(true);
    setRoutePreference('auto');
    setCustomModuleUrl('');
    initializeForFile(null);
  }

  function goBackToUploadStep() {
    if (busy) return;
    setActiveStep('upload');
  }

  function goToSettingsStep() {
    if (!file || busy) return;
    setActiveStep('settings');
  }

  function startConversionStep() {
    if (!canConvert || busy) return;
    setActiveStep('converting');
    void runConversion();
  }

  function handleTargetMimeChange(nextTargetMime: string) {
    setTargetMime(nextTargetMime);
    if (file) {
      const nextTarget = targetOptions.find((option) => option.value === nextTargetMime);
      markConfigurationChanged(`Target format set to ${nextTarget?.label ?? nextTargetMime}. Ready to convert.`);
    }
  }

  function handleOutputBaseNameChange(nextOutputBaseName: string) {
    setOutputBaseName(nextOutputBaseName);
    if (file) {
      markConfigurationChanged('Output file name updated. Ready to convert.');
    }
  }

  function handleImageWidthChange(nextImageWidth: string) {
    setImageWidth(nextImageWidth);
    if (file) {
      markConfigurationChanged('Image size updated. Ready to convert.');
    }
  }

  function handleImageHeightChange(nextImageHeight: string) {
    setImageHeight(nextImageHeight);
    if (file) {
      markConfigurationChanged('Image size updated. Ready to convert.');
    }
  }

  function handleKeepAspectRatioChange(nextKeepAspectRatio: boolean) {
    setKeepAspectRatio(nextKeepAspectRatio);
    if (file) {
      markConfigurationChanged('Aspect ratio preference updated. Ready to convert.');
    }
  }

  function handleQualityChange(nextQuality: number) {
    setQuality(nextQuality);
    if (file) {
      markConfigurationChanged('Image quality updated. Ready to convert.');
    }
  }

  function handleTrimStartChange(nextTrimStart: string) {
    setTrimStart(nextTrimStart);
    if (file) {
      markConfigurationChanged('Trim settings updated. Ready to convert.');
    }
  }

  function handleTrimEndChange(nextTrimEnd: string) {
    setTrimEnd(nextTrimEnd);
    if (file) {
      markConfigurationChanged('Trim settings updated. Ready to convert.');
    }
  }

  function handleRoutePreferenceChange(nextRoutePreference: RoutePreference) {
    setRoutePreference(nextRoutePreference);
    if (file) {
      markConfigurationChanged('Route preference updated. Ready to convert.');
    }
  }

  function handleEnableWasmFallbackChange(enabled: boolean) {
    setEnableWasmFallback(enabled);
    if (file) {
      markConfigurationChanged(
        enabled
          ? 'ffmpeg.wasm fallback enabled. Ready to convert.'
          : 'ffmpeg.wasm fallback disabled. Ready to convert.',
      );
    }
  }

  function handleCustomModuleUrlChange(nextCustomModuleUrl: string) {
    setCustomModuleUrl(nextCustomModuleUrl);
    if (file) {
      markConfigurationChanged('Fallback module URL updated. Ready to convert.');
    }
  }

  function navigate(next: Page) {
    if (next === 'app') {
      window.location.hash = '#/app';
    } else if (next === 'privacy') {
      window.location.hash = '#/privacy';
    } else if (next === 'terms') {
      window.location.hash = '#/terms';
    } else if (next === 'docs') {
      window.location.hash = '#/docs';
    } else {
      window.location.hash = '';
    }

    setPage(next);
  }

  function handleProgress(activity: ConversionActivity, tone: ActivityTone = 'info') {
    const normalizedProgress = clampProgress(activity.progress);
    const detail = activity.detail?.trim() || '';
    const rawOutput = activity.rawOutput?.trim() || '';

    setStatus(activity.message);
    setStatusDetail(detail);
    setProgress(normalizedProgress);
    setStatusSource(activity.source);
    setLastActivityAt(Date.now());
    setActivityLog((previous) => {
      const nextEntries = [...previous];
      const timestamp = formatEventTime(new Date());
      let lastMilestoneIndex = -1;

      for (let index = nextEntries.length - 1; index >= 0; index -= 1) {
        if (nextEntries[index].variant === 'milestone') {
          lastMilestoneIndex = index;
          break;
        }
      }

      let nextId = nextEntries[nextEntries.length - 1]?.id ?? 0;
      const lastMilestone = lastMilestoneIndex >= 0 ? nextEntries[lastMilestoneIndex] : undefined;

      if (tone === 'info' && lastMilestone && lastMilestone.message === activity.message && lastMilestone.tone === tone) {
        nextEntries[lastMilestoneIndex] = {
          ...lastMilestone,
          detail: detail || lastMilestone.detail,
          progress: normalizedProgress,
          timestamp,
          source: activity.source ?? lastMilestone.source,
        };
      } else {
        nextId += 1;
        nextEntries.push({
          id: nextId,
          message: activity.message,
          detail,
          progress: normalizedProgress,
          tone,
          timestamp,
          variant: 'milestone',
          source: activity.source,
        });
      }

      if (rawOutput) {
        nextId += 1;
        nextEntries.push({
          id: nextId,
          message: rawOutput,
          detail: `${activity.message}${detail ? ` • ${detail}` : ''}`,
          progress: normalizedProgress,
          tone,
          timestamp,
          variant: 'raw',
          source: activity.source,
        });
      }

      return nextEntries.slice(-MAX_ACTIVITY_HISTORY_LENGTH);
    });
  }

  function markConfigurationChanged(message: string) {
    setResult(null);
    setProgress(0);
    setStatus(message);
    setStatusDetail('The conversion configuration changed. Start a new run when ready.');
    setStatusMode(file ? 'ready' : 'idle');
    setStatusSource(undefined);
    setLastActivityAt(Date.now());
    if (!busy && file) {
      setActiveStep('settings');
    }
  }

  function cancelConversion() {
    if (!busy || cancelRequested) return;
    setCancelRequested(true);
    handleProgress(
      {
        progress: progressRef.current,
        message: 'Cancel requested',
        detail: 'Stopping the current conversion and cleaning up local work.',
        source: statusSource,
      },
      'info',
    );
    activeAbortController.current?.abort();
  }

  async function runConversion() {
    if (!file || !targetMime || trimValidationError) return;

    const abortController = new AbortController();
    activeAbortController.current = abortController;
    setActiveStep('converting');
    setBusy(true);
    setCancelRequested(false);
    setStatusMode('working');
    setLogOpen(false);
    setResult(null);
    setStatusSource(routeSource);
    setActivityLog(() => {
      let nextId = 0;
      const nextEntries: ActivityEntry[] = [
        {
          id: (nextId += 1),
          message: `Starting ${routeDisplayLabel.toLowerCase()} conversion`,
          detail: `${routePreferenceLabel(routePreference)} is active while preparing ${file.name} for local conversion.`,
          progress: 0,
          tone: 'info',
          timestamp: formatEventTime(new Date()),
          variant: 'milestone',
          source: routeSource,
        },
      ];
      const optionsLabel = selectedAdjustments.join(' • ');
      if (optionsLabel) {
        nextEntries.push({
          id: (nextId += 1),
          message: optionsLabel,
          detail: 'Selected output adjustments for this conversion.',
          progress: 0,
          tone: 'info',
          timestamp: formatEventTime(new Date()),
          variant: 'milestone',
          source: routeSource,
        });
      }
      return nextEntries;
    });
    handleProgress(
      {
        progress: 0.05,
        message: 'Preparing conversion job',
        detail: `Initializing ${routeDisplayLabel.toLowerCase()}.`,
        source: routeSource,
      },
    );

    if (routeDecision === 'wasm') {
      const sourceSize = formatBytes(file.size);
      handleProgress({
        progress: 0.08,
        message: 'Using ffmpeg.wasm single-thread route',
        detail:
          `This job is running client-side in WebAssembly (${sourceSize} input). Long video conversions can take significantly longer than desktop FFmpeg.`,
        source: 'ffmpeg',
      });
    }

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
                signal: abortController.signal,
              })
            : await convertViaMediaRecorder({
                file,
                targetMime,
                onProgress: handleProgress,
                signal: abortController.signal,
              });
      } else if (routeDecision === 'wasm' && enableWasmFallback) {
        next = await convertWithWasmFallback({
          file,
          targetMime,
          wasmModuleUrl: moduleUrl,
          options: requestedOptions,
          onProgress: handleProgress,
          signal: abortController.signal,
        });
      } else {
        throw new Error(routeReason);
      }

      setResult({
        ...next,
        outputName: buildOutputName(file.name, targetMime, requestedOptions.outputBaseName),
      });
      setStatusMode('success');
      setActiveStep('results');
      handleProgress(
        {
          progress: 1,
          message: 'Conversion complete — preview ready below',
          detail: 'Review the preview or download the converted file.',
          source: routeSource,
        },
        'success',
      );
    } catch (error) {
      if (isConversionAbortError(error)) {
        setStatusMode('canceled');
        handleProgress({
          progress: 0,
          message: 'Conversion canceled',
          detail: 'The conversion was stopped before a new file was generated.',
          source: routeSource,
        });
      } else {
        setStatusMode('error');
        handleProgress(
          {
            progress: 0,
            message: 'Conversion failed',
            detail: error instanceof Error ? error.message : 'Unknown error',
            source: routeSource,
          },
          'error',
        );
        setLogOpen(true);
      }
    } finally {
      activeAbortController.current = null;
      setCancelRequested(false);
      setBusy(false);
    }
  }

  if (page === 'landing') {
    return (
      <>
        <UpdateNotification />
        <LandingPage onOpenApp={() => navigate('app')} />
      </>
    );
  }

  if (page === 'privacy') {
    return (
      <>
        <UpdateNotification />
        <PrivacyPage />
      </>
    );
  }

  if (page === 'terms') {
    return (
      <>
        <UpdateNotification />
        <TermsPage />
      </>
    );
  }

  if (page === 'docs') {
    return <DocsPage />;
  }

  return (
    <>
      <UpdateNotification />
      <main className="page">
      <header className="topbar">
        <a className="brand brand-link" href="#">
          <LogoIcon size={28} />
          {APP_NAME}
        </a>
        <HamburgerMenu />
      </header>

      <section className="workspace-hero">
        <div>
          <span className="eyebrow">Converter workspace</span>
          <h1>Convert files locally.</h1>
          <p className="hero-text compact">
            Choose a file, compare the route, inspect the size change, and convert.
          </p>
        </div>
        <div className={`route-chip route-${routeDecision}`}>{routeDisplayLabel}</div>
      </section>

      <WizardStepper steps={wizardSteps} />

      <section className="wizard-panel">
        {activeStep === 'upload' ? (
          <UploadStep
            busy={busy}
            detectedType={file ? mediaType : '—'}
            sourceFormat={file?.type || '—'}
            fileSize={file ? formatBytes(file.size) : '—'}
            canContinue={Boolean(file)}
            onFileChange={initializeForFile}
            onContinue={goToSettingsStep}
          />
        ) : null}

        {activeStep === 'settings' ? (
          <SettingsStep
            busy={busy}
            mediaType={mediaType}
            selectedFileSummary={file ? `${file.name} (${formatBytes(file.size)})` : 'No file selected'}
            targetMime={targetMime}
            targetOptions={targetOptions}
            outputBaseName={outputBaseName}
            outputNamePlaceholder={file ? stripExtension(file.name) : 'converted-file'}
            imageWidth={imageWidth}
            imageHeight={imageHeight}
            keepAspectRatio={keepAspectRatio}
            quality={quality}
            trimStart={trimStart}
            trimEnd={trimEnd}
            trimValidationError={trimValidationError}
            routePreference={routePreference}
            enableWasmFallback={enableWasmFallback}
            customModuleUrl={customModuleUrl}
            defaultModuleUrl={defaultModuleUrl}
            outputFileName={outputFileName}
            selectedAdjustments={selectedAdjustments}
            routeDisplayLabel={routeDisplayLabel}
            routePreferenceLabel={routePreferenceLabel(routePreference)}
            routeReason={routeReason}
            canConvert={canConvert}
            onTargetMimeChange={handleTargetMimeChange}
            onOutputBaseNameChange={handleOutputBaseNameChange}
            onImageWidthChange={handleImageWidthChange}
            onImageHeightChange={handleImageHeightChange}
            onKeepAspectRatioChange={handleKeepAspectRatioChange}
            onQualityChange={handleQualityChange}
            onTrimStartChange={handleTrimStartChange}
            onTrimEndChange={handleTrimEndChange}
            onRoutePreferenceChange={handleRoutePreferenceChange}
            onEnableWasmFallbackChange={handleEnableWasmFallbackChange}
            onCustomModuleUrlChange={handleCustomModuleUrlChange}
            onBack={goBackToUploadStep}
            onConvert={startConversionStep}
          />
        ) : null}

        {activeStep === 'converting' ? (
          <ConvertingStep
            status={status}
            statusMode={statusMode}
            statusIndicator={statusIndicator}
            progress={progress}
            busy={busy}
            liveStatusDetail={liveStatusDetail}
            statusSource={statusSource}
            recentMilestones={recentMilestones}
            logOpen={logOpen}
            rawOutputEntries={rawOutputEntries}
            cancelRequested={cancelRequested}
            canConvert={canConvert}
            onToggleLog={() => setLogOpen((open) => !open)}
            onCancel={cancelConversion}
            onConvertAgain={startConversionStep}
            onRestart={restartConverter}
            sourceLabel={sourceLabel}
          />
        ) : null}

        {activeStep === 'results' ? (
          <ResultsStep
            hasResult={Boolean(result && downloadUrl)}
            preview={result && downloadUrl ? previewForResult(downloadUrl, result) : null}
            routeLabel={result?.route || '—'}
            inputSizeLabel={file ? formatBytes(file.size) : '—'}
            outputSizeLabel={result ? formatBytes(result.blob.size) : '—'}
            sizeChange={sizeChange}
            mimeTypeLabel={result?.blob.type || 'unknown'}
            sizeGuidance={sizeGuidance}
            downloadUrl={downloadUrl}
            resultOutputName={resultOutputName}
            onRestart={restartConverter}
          />
        ) : null}

        <details className="card info-details">
          <summary>Technical support details</summary>
          <p className="muted">
            Browser capability details are available here if you need to understand route
            selection or troubleshoot a conversion.
          </p>
          <pre>{JSON.stringify(capabilities, null, 2)}</pre>
        </details>
      </section>

      <Footer />
    </main>
    </>
  );
}
