type UploadStepProps = {
  busy: boolean;
  detectedType: string;
  sourceFormat: string;
  fileSize: string;
  canContinue: boolean;
  onFileChange: (file: File | null) => void;
  onContinue: () => void;
};

export function UploadStep({
  busy,
  detectedType,
  sourceFormat,
  fileSize,
  canContinue,
  onFileChange,
  onContinue,
}: UploadStepProps) {
  return (
    <div className="card wizard-card">
      <h2>1. Choose file</h2>
      <p className="muted">Pick a local file to begin. Nothing is uploaded to a server.</p>
      <label className="field">
        <span>Input file</span>
        <input
          type="file"
          disabled={busy}
          onChange={(event) => onFileChange(event.target.files?.[0] ?? null)}
        />
      </label>
      <div className="meta-grid">
        <div>
          <span className="meta-label">Detected type</span>
          <strong>{detectedType}</strong>
        </div>
        <div>
          <span className="meta-label">Source format</span>
          <strong>{sourceFormat}</strong>
        </div>
        <div>
          <span className="meta-label">File size</span>
          <strong>{fileSize}</strong>
        </div>
      </div>
      <div className="wizard-actions">
        <button type="button" onClick={onContinue} disabled={!canContinue || busy}>
          Continue to settings
        </button>
      </div>
    </div>
  );
}
