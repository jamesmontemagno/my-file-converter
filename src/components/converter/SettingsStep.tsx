type RoutePreference = 'auto' | 'native' | 'ffmpeg';

type FormatOption = {
  value: string;
  label: string;
};

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
  trimValidationError: string;
  routePreference: RoutePreference;
  customModuleUrl: string;
  defaultModuleUrl: string;
  outputFileName: string;
  selectedAdjustments: string[];
  routeDisplayLabel: string;
  routePreferenceLabel: string;
  routeReason: string;
  canConvert: boolean;
  onTargetMimeChange: (value: string) => void;
  onOutputBaseNameChange: (value: string) => void;
  onImageWidthChange: (value: string) => void;
  onImageHeightChange: (value: string) => void;
  onKeepAspectRatioChange: (value: boolean) => void;
  onQualityChange: (value: number) => void;
  onTrimStartChange: (value: string) => void;
  onTrimEndChange: (value: string) => void;
  onRoutePreferenceChange: (value: RoutePreference) => void;
  onCustomModuleUrlChange: (value: string) => void;
  onBack: () => void;
  onConvert: () => void;
};

const routePreferenceOptions: {
  value: RoutePreference;
  label: string;
  description: string;
}[] = [
  {
    value: 'auto',
    label: 'Auto',
    description: 'Use the fastest route available for the selected format. Falls back to ffmpeg.wasm when needed.',
  },
  {
    value: 'native',
    label: 'Prefer browser-native',
    description: 'Use browser-native encoding when possible. Falls back to ffmpeg.wasm for unsupported formats.',
  },
  {
    value: 'ffmpeg',
    label: 'Use ffmpeg.wasm',
    description: 'Always use the WebAssembly encoder, even when faster browser-native encoding is available.',
  },
];

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
  trimValidationError,
  routePreference,
  customModuleUrl,
  defaultModuleUrl,
  outputFileName,
  selectedAdjustments,
  routeDisplayLabel,
  routePreferenceLabel,
  routeReason,
  canConvert,
  onTargetMimeChange,
  onOutputBaseNameChange,
  onImageWidthChange,
  onImageHeightChange,
  onKeepAspectRatioChange,
  onQualityChange,
  onTrimStartChange,
  onTrimEndChange,
  onRoutePreferenceChange,
  onCustomModuleUrlChange,
  onBack,
  onConvert,
}: SettingsStepProps) {
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
                onChange={(event) => onTrimStartChange(event.target.value)}
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
                onChange={(event) => onTrimEndChange(event.target.value)}
                placeholder="Leave blank for full length"
              />
            </label>
          </div>
          <small>Trim controls use ffmpeg when the native browser route cannot apply them.</small>
          {trimValidationError ? <p className="form-error">{trimValidationError}</p> : null}
        </div>
      ) : null}

      <details className="field">
        <summary>Rendering options</summary>
        <div className="field route-preference-field">
          <span>Conversion route preference</span>
          <div className="route-preference-options">
            {routePreferenceOptions.map((option) => (
              <label
                key={option.value}
                className={`route-preference-option${routePreference === option.value ? ' is-selected' : ''}`}
              >
                <input
                  type="radio"
                  name="route-preference"
                  value={option.value}
                  checked={routePreference === option.value}
                  disabled={busy}
                  onChange={(event) => onRoutePreferenceChange(event.target.value as RoutePreference)}
                />
                <span>
                  <strong>{option.label}</strong>
                  <small>{option.description}</small>
                </span>
              </label>
            ))}
          </div>
        </div>
        <label className="field">
          <span>Override fallback module URL</span>
          <input
            type="url"
            value={customModuleUrl}
            onChange={(event) => onCustomModuleUrlChange(event.target.value)}
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

      <div className="card route-summary-card">
        <p className="muted">
          Route selected: <strong>{routeDisplayLabel}</strong>
        </p>
        <p className="muted">
          Route preference: <strong>{routePreferenceLabel}</strong>
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
