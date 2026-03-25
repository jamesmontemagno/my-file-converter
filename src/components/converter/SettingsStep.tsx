type FormatOption = {
  value: string;
  label: string;
  supported: boolean;
};

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
  outputFileName: string;
  selectedAdjustments: string[];
  routeDisplayLabel: string;
  routeReason: string;
  canConvert: boolean;
  onTargetMimeChange: (value: string) => void;
  onOutputBaseNameChange: (value: string) => void;
  onImageWidthChange: (value: string) => void;
  onImageHeightChange: (value: string) => void;
  onKeepAspectRatioChange: (value: boolean) => void;
  onQualityChange: (value: number) => void;
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
  outputFileName,
  selectedAdjustments,
  routeDisplayLabel,
  routeReason,
  canConvert,
  onTargetMimeChange,
  onOutputBaseNameChange,
  onImageWidthChange,
  onImageHeightChange,
  onKeepAspectRatioChange,
  onQualityChange,
  onBack,
  onConvert,
}: SettingsStepProps) {
  const selectedOption = targetOptions.find((option) => option.value === targetMime);
  const guidance = guidanceForFormat(targetMime, mediaType);

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
              {option.label}{option.supported ? '' : ' (Unsupported in this browser)'}
            </option>
          ))}
        </select>
        {selectedOption && !selectedOption.supported ? (
          <p className="form-error">This format is not supported in your current browser.</p>
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
          <p className="muted">Trim controls are not available in this native-only release yet.</p>
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
