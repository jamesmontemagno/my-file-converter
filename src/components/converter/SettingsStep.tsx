import { useState } from 'react';
import { BridgeError, parseBridgeStartupLine } from '../../bridge/client';
import { BridgeLaunchActions } from '../BridgeLaunchActions';

type FormatOption = {
  value: string;
  label: string;
  supported: boolean;
};

type BridgeState = 'disconnected' | 'connecting' | 'available' | 'missing-ffmpeg' | 'error';

function guidanceForFormat(targetMime: string, mediaType: string) {
  if (mediaType === 'image') {
    if (targetMime === 'image/png') {
      return {
        bestFor: 'Logos, screenshots, and sharp graphics',
        tradeoff: 'Usually larger files than JPEG or WebP.',
      };
    }

    if (targetMime === 'image/jpeg') {
      return {
        bestFor: 'Photos and broad compatibility',
        tradeoff: 'Lossy compression can introduce artifacts.',
      };
    }

    if (targetMime === 'image/webp') {
      return {
        bestFor: 'Smaller web images with good visual quality',
        tradeoff: 'Some legacy apps may not support WebP.',
      };
    }

    if (targetMime === 'image/avif') {
      return {
        bestFor: 'Maximum compression for modern browsers',
        tradeoff: 'Encoding and decoding can be slower on older devices.',
      };
    }

    if (targetMime === 'image/gif') {
      return {
        bestFor: 'Simple graphics, icons, and legacy compatibility',
        tradeoff: 'Limited to 256 colors. Not ideal for photos.',
      };
    }

    if (targetMime === 'image/bmp') {
      return {
        bestFor: 'Uncompressed pixel-perfect output for editing tools',
        tradeoff: 'Very large files with no compression.',
      };
    }
  }

  if (mediaType === 'audio') {
    if (targetMime.includes('wav')) {
      return {
        bestFor: 'Lossless exports for editing, archiving, and production workflows',
        tradeoff: 'Files are much larger than MP3/Opus formats.',
      };
    }

    if (targetMime.includes('mpeg')) {
      return {
        bestFor: 'Universal playback and sharing across apps/devices',
        tradeoff: 'Uses software encoding in-browser and may take longer on large files.',
      };
    }

    if (targetMime.includes('ogg')) {
      return {
        bestFor: 'Open audio workflows and lightweight files',
        tradeoff: 'Playback support is weaker in some Apple-first tools.',
      };
    }

    if (targetMime.includes('webm')) {
      return {
        bestFor: 'Web playback and efficient speech/music delivery',
        tradeoff: 'Native support in older desktop software may vary.',
      };
    }

    if (targetMime.includes('mp4')) {
      return {
        bestFor: 'Maximum compatibility across devices and apps',
        tradeoff: 'File size can be larger than Opus-based outputs.',
      };
    }
  }

  if (mediaType === 'video') {
    if (targetMime.includes('mpeg')) {
      return {
        bestFor: 'Extracting a shareable MP3 track from video footage',
        tradeoff: 'Audio is extracted and encoded in software, so long videos can take time.',
      };
    }

    if (targetMime.includes('wav')) {
      return {
        bestFor: 'Extracting lossless audio from video for editing',
        tradeoff: 'WAV exports are large and best for quality-first workflows.',
      };
    }

    if (targetMime.includes('vp9')) {
      return {
        bestFor: 'Smaller files at similar quality for modern browsers',
        tradeoff: 'Encoding may take longer than VP8 or H.264.',
      };
    }

    if (targetMime.includes('vp8')) {
      return {
        bestFor: 'Balanced WebM compatibility and speed',
        tradeoff: 'Often larger than VP9 for the same visual quality.',
      };
    }

    if (targetMime.includes('mp4')) {
      return {
        bestFor: 'Broad playback compatibility across platforms',
        tradeoff: 'May not produce the smallest file for web delivery.',
      };
    }

    if (targetMime.includes('av01')) {
      return {
        bestFor: 'Next-generation compression with excellent quality at low bitrates',
        tradeoff: 'Encoding is slower and requires WebCodecs hardware support.',
      };
    }
  }

  return {
    bestFor: 'General conversion',
    tradeoff: 'Output quality and size can vary by browser encoder.',
  };
}

type SettingsStepProps = {
  busy: boolean;
  mediaType: string;
  selectedFileSummary: string;
  targetMime: string;
  targetOptions: FormatOption[];
  outputBaseName: string;
  outputNamePlaceholder: string;
  imageWidth: string;
  imageHeight: string;
  keepAspectRatio: boolean;
  quality: number;
  trimStart: string;
  trimEnd: string;
  channelMode: string;
  outputFileName: string;
  selectedAdjustments: string[];
  routeDisplayLabel: string;
  routeReason: string;
  routePreference: 'auto' | 'browser';
  bridgeState: BridgeState;
  bridgeUrl: string;
  bridgeToken: string;
  bridgeDetail: string;
  canConvert: boolean;
  onTargetMimeChange: (value: string) => void;
  onOutputBaseNameChange: (value: string) => void;
  onImageWidthChange: (value: string) => void;
  onImageHeightChange: (value: string) => void;
  onKeepAspectRatioChange: (value: boolean) => void;
  onQualityChange: (value: number) => void;
  onTrimStartChange: (value: string) => void;
  onTrimEndChange: (value: string) => void;
  onChannelModeChange: (value: string) => void;
  onRoutePreferenceChange: (value: 'auto' | 'browser') => void;
  onBridgeUrlChange: (value: string) => void;
  onBridgeTokenChange: (value: string) => void;
  onConnectBridge: () => void;
  onBack: () => void;
  onConvert: () => void;
};

export function SettingsStep({
  busy,
  mediaType,
  selectedFileSummary,
  targetMime,
  targetOptions,
  outputBaseName,
  outputNamePlaceholder,
  imageWidth,
  imageHeight,
  keepAspectRatio,
  quality,
  trimStart,
  trimEnd,
  channelMode,
  outputFileName,
  selectedAdjustments,
  routeDisplayLabel,
  routeReason,
  routePreference,
  bridgeState,
  bridgeUrl,
  bridgeToken,
  bridgeDetail,
  canConvert,
  onTargetMimeChange,
  onOutputBaseNameChange,
  onImageWidthChange,
  onImageHeightChange,
  onKeepAspectRatioChange,
  onQualityChange,
  onTrimStartChange,
  onTrimEndChange,
  onChannelModeChange,
  onRoutePreferenceChange,
  onBridgeUrlChange,
  onBridgeTokenChange,
  onConnectBridge,
  onBack,
  onConvert,
}: SettingsStepProps) {
  const selectedOption = targetOptions.find((option) => option.value === targetMime);
  const guidance = guidanceForFormat(targetMime, mediaType);
  const [startupLine, setStartupLine] = useState('');
  const [startupLineDetail, setStartupLineDetail] = useState('');

  function handleStartupLineChange(value: string) {
    setStartupLine(value);
    if (!value.trim()) {
      setStartupLineDetail('');
      return;
    }

    try {
      const connection = parseBridgeStartupLine(value);
      onBridgeUrlChange(connection.baseUrl);
      onBridgeTokenChange(connection.token);
      setStartupLine('');
      setStartupLineDetail('Bridge URL and pairing token filled in. Select Connect bridge.');
    } catch (error) {
      setStartupLineDetail(error instanceof BridgeError ? error.message : 'Could not read the bridge startup line.');
    }
  }

  return (
    <div className="card wizard-card">
      <h2>2. Set options</h2>
      <div className="selection-summary">
        <span className="meta-label">Selected file</span>
        <strong>{selectedFileSummary}</strong>
      </div>

      <label className="field">
        <span>Target format</span>
        <select
          value={targetMime}
          onChange={(event) => onTargetMimeChange(event.target.value)}
          disabled={!targetOptions.length || busy}
        >
          {targetOptions.map((option) => (
            <option key={option.value} value={option.value} disabled={!option.supported}>
              {option.label}{option.supported ? '' : ' (Unavailable with the current route)'}
            </option>
          ))}
        </select>
        {selectedOption && !selectedOption.supported ? (
          <p className="form-error">This format is unavailable with the selected conversion route.</p>
        ) : null}
      </label>

      <div className="format-guidance-card">
        <span className="meta-label">Format guidance</span>
        <p>
          <strong>Best for:</strong> {guidance.bestFor}
        </p>
        <p>
          <strong>Tradeoff:</strong> {guidance.tradeoff}
        </p>
      </div>

      <label className="field">
        <span>Output file name</span>
        <input
          type="text"
          value={outputBaseName}
          disabled={busy}
          onChange={(event) => onOutputBaseNameChange(event.target.value)}
          placeholder={outputNamePlaceholder}
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
                onChange={(event) => onImageWidthChange(event.target.value)}
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
                onChange={(event) => onImageHeightChange(event.target.value)}
                placeholder="Original height"
              />
            </label>
          </div>
          <label className="checkbox">
            <input
              type="checkbox"
              checked={keepAspectRatio}
              disabled={busy}
              onChange={(event) => onKeepAspectRatioChange(event.target.checked)}
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
              onChange={(event) => onQualityChange(Number(event.target.value))}
            />
            <output>{quality.toFixed(2)}</output>
          </label>
        </div>
      ) : null}

      {(mediaType === 'audio' || mediaType === 'video') ? (
        <div className="option-section">
          <h3>Media options</h3>
          <div className="option-grid">
            <label className="field">
              <span>Trim start (seconds)</span>
              <input
                type="number"
                min="0"
                step="0.1"
                inputMode="decimal"
                value={trimStart}
                disabled={busy}
                onChange={(event) => onTrimStartChange(event.target.value)}
                placeholder="0"
              />
            </label>
            <label className="field">
              <span>Trim end (seconds)</span>
              <input
                type="number"
                min="0"
                step="0.1"
                inputMode="decimal"
                value={trimEnd}
                disabled={busy}
                onChange={(event) => onTrimEndChange(event.target.value)}
                placeholder="End of file"
              />
            </label>
          </div>
          <small>Leave blank to use the full duration. Set trim end to stop at a specific time.</small>

          {(targetMime === 'audio/mpeg' || targetMime === 'audio/wav' || targetMime.startsWith('audio/')) ? (
            <label className="field">
              <span>Channels</span>
              <select
                value={channelMode}
                disabled={busy}
                onChange={(event) => onChannelModeChange(event.target.value)}
              >
                <option value="auto">Auto (keep source)</option>
                <option value="mono">Mono</option>
                <option value="stereo">Stereo</option>
              </select>
            </label>
          ) : null}
        </div>
      ) : null}

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

      <fieldset className="route-preference-field" disabled={busy}>
        <legend>Conversion engine</legend>
        <div className="route-preference-options">
          <label className={`route-preference-option ${routePreference === 'auto' ? 'is-selected' : ''}`}>
            <input
              type="radio"
              name="route-preference"
              value="auto"
              checked={routePreference === 'auto'}
              onChange={() => onRoutePreferenceChange('auto')}
            />
            <span>
              <strong>Prefer Local FFmpeg Bridge</strong>
              <small>
                {bridgeState === 'available'
                  ? 'Connected. Supported conversions use FFmpeg on this device.'
                  : 'Connect a running bridge below, otherwise browser conversion stays selected.'}
              </small>
            </span>
          </label>
          <label className={`route-preference-option ${routePreference === 'browser' ? 'is-selected' : ''}`}>
            <input
              type="radio"
              name="route-preference"
              value="browser"
              checked={routePreference === 'browser'}
              onChange={() => onRoutePreferenceChange('browser')}
            />
            <span>
              <strong>Browser only</strong>
              <small>Use the current browser-native and software conversion paths.</small>
            </span>
          </label>
        </div>
      </fieldset>

      <div className="bridge-setup-card">
        <div>
          <span className="meta-label">Local FFmpeg Bridge</span>
          <strong>
            {bridgeState === 'available'
              ? 'Connected'
              : bridgeState === 'connecting'
                ? 'Connecting…'
                : bridgeState === 'missing-ffmpeg'
                  ? 'FFmpeg missing'
                  : 'Not connected'}
          </strong>
        </div>
        <p className="muted">
          Start the lightweight LocalMorph Bridge, then paste its complete <code>LOCALMORPH_BRIDGE=...</code>
          line below. Files stay on this device and are sent only to the running bridge.
        </p>
        <label className="field">
          <span>Bridge startup line</span>
          <input
            type="text"
            value={startupLine}
            placeholder={'LOCALMORPH_BRIDGE={"baseUrl":"http://127.0.0.1:49321","token":"..."}'}
            autoComplete="off"
            spellCheck={false}
            disabled={bridgeState === 'connecting'}
            onChange={(event) => handleStartupLineChange(event.target.value)}
          />
          <small aria-live="polite">{startupLineDetail || 'Copy and paste the entire line printed by the tool.'}</small>
        </label>
        <p className="muted">Or enter the values manually:</p>
        <label className="field">
          <span>Bridge URL</span>
          <input
            type="url"
            value={bridgeUrl}
            placeholder="http://127.0.0.1:49321"
            disabled={bridgeState === 'connecting'}
            onChange={(event) => onBridgeUrlChange(event.target.value)}
          />
        </label>
        <label className="field">
          <span>Pairing token</span>
          <input
            type="text"
            value={bridgeToken}
            placeholder="Shown when the bridge starts"
            disabled={bridgeState === 'connecting'}
            onChange={(event) => onBridgeTokenChange(event.target.value)}
          />
        </label>
        <button
          type="button"
          className="ghost-button"
          onClick={onConnectBridge}
          disabled={bridgeState === 'connecting'}
        >
          {bridgeState === 'connecting' ? 'Checking bridge…' : 'Connect bridge'}
        </button>
        <div className="bridge-setup-actions">
          <BridgeLaunchActions />
          <a className="bridge-setup-link" href="#/docs">
            View bridge setup
          </a>
        </div>
        {bridgeDetail ? (
          <p className={bridgeState === 'error' || bridgeState === 'missing-ffmpeg' ? 'form-error' : 'muted'}>
            {bridgeDetail}
          </p>
        ) : null}
        {bridgeState === 'missing-ffmpeg' ? (
          <p className="muted">
            Install FFmpeg, make it available on your PATH, then restart LocalMorph Bridge. On Windows,
            use a trusted FFmpeg distribution; on macOS use Homebrew; on Linux use your distribution&apos;s
            package manager.
          </p>
        ) : null}
      </div>

      <div className="card route-summary-card">
        <p className="muted">
          Route selected: <strong>{routeDisplayLabel}</strong>
        </p>
        <p className="muted">{routeReason}</p>
      </div>

      <div className="wizard-actions">
        <button type="button" className="ghost-button" onClick={onBack} disabled={busy}>
          Back to file
        </button>
        <button type="button" onClick={onConvert} disabled={!canConvert || busy}>
          Convert file
        </button>
      </div>
    </div>
  );
}
