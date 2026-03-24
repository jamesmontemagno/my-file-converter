type FormatOption = {
  value: string;
  label: string;
  supported: boolean;
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
