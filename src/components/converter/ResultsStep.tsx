import type { ReactNode } from 'react';

type SizeChangeSummary = {
  trend: 'smaller' | 'larger' | 'same';
  summaryLabel: string;
};

type ResultsStepProps = {
  hasResult: boolean;
  preview: ReactNode;
  routeLabel: string;
  inputSizeLabel: string;
  outputSizeLabel: string;
  sizeChange: SizeChangeSummary | null;
  mimeTypeLabel: string;
  sizeGuidance: string;
  downloadUrl: string;
  resultOutputName: string;
  onRestart: () => void;
};

export function ResultsStep({
  hasResult,
  preview,
  routeLabel,
  inputSizeLabel,
  outputSizeLabel,
  sizeChange,
  mimeTypeLabel,
  sizeGuidance,
  downloadUrl,
  resultOutputName,
  onRestart,
}: ResultsStepProps) {
  return (
    <div className="card wizard-card">
      <h2>4. Preview and download</h2>
      {hasResult ? (
        <>
          <div className="preview-shell">{preview}</div>
          <div className="meta-grid">
            <div>
              <span className="meta-label">Route</span>
              <strong>{routeLabel}</strong>
            </div>
            <div>
              <span className="meta-label">Input size</span>
              <strong>{inputSizeLabel}</strong>
            </div>
            <div>
              <span className="meta-label">Output size</span>
              <strong>{outputSizeLabel}</strong>
            </div>
            <div>
              <span className="meta-label">Size change</span>
              <strong>{sizeChange?.summaryLabel ?? '—'}</strong>
            </div>
            <div>
              <span className="meta-label">MIME type</span>
              <strong>{mimeTypeLabel}</strong>
            </div>
          </div>
          {sizeChange ? (
            <p className={`result-guidance result-guidance-${sizeChange.trend}`}>{sizeGuidance}</p>
          ) : null}
          <div className="wizard-actions">
            <a className="download-button" href={downloadUrl} download={resultOutputName}>
              Download {resultOutputName}
            </a>
            <button type="button" className="ghost-button" onClick={onRestart}>
              Start over
            </button>
          </div>
        </>
      ) : (
        <div className="empty-state">
          <strong>No output yet</strong>
          <p>After conversion, the result will appear here so the user can inspect it before downloading.</p>
        </div>
      )}
    </div>
  );
}
