type StatusMode = 'idle' | 'ready' | 'working' | 'success' | 'error' | 'canceled';
type ActivityTone = 'info' | 'success' | 'error';

type ActivityEntry = {
  id: number;
  message: string;
  detail?: string;
  progress: number;
  tone: ActivityTone;
  timestamp: string;
  source?: 'native' | 'encoder';
};

type RawOutputEntry = {
  id: number;
  timestamp: string;
  message: string;
};

type ConvertingStepProps = {
  status: string;
  statusMode: StatusMode;
  statusIndicator: { label: string; detail: string };
  progress: number;
  busy: boolean;
  liveStatusDetail: string;
  statusSource?: 'native' | 'encoder';
  recentMilestones: ActivityEntry[];
  logOpen: boolean;
  rawOutputEntries: RawOutputEntry[];
  cancelRequested: boolean;
  canConvert: boolean;
  onToggleLog: () => void;
  onCancel: () => void;
  onConvertAgain: () => void;
  onAdjustSettings: () => void;
  onRestart: () => void;
  sourceLabel: (source?: 'native' | 'encoder') => string;
};

export function ConvertingStep({
  status,
  statusMode,
  statusIndicator,
  progress,
  busy,
  liveStatusDetail,
  statusSource,
  recentMilestones,
  logOpen,
  rawOutputEntries,
  cancelRequested,
  canConvert,
  onToggleLog,
  onCancel,
  onConvertAgain,
  onAdjustSettings,
  onRestart,
  sourceLabel,
}: ConvertingStepProps) {
  return (
    <div className="card wizard-card">
      <div className="status-header">
        <div>
          <h2>3. Convert and monitor</h2>
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
        <span>{busy ? liveStatusDetail : statusIndicator.detail}</span>
      </div>
      <div className="live-status-card">
        <div className="live-status-header">
          <div>
            <span className="meta-label">Live stage</span>
            <strong>{status}</strong>
          </div>
          {statusSource ? <span className="live-source-chip">{sourceLabel(statusSource)}</span> : null}
        </div>
        <p className="muted">{liveStatusDetail}</p>
      </div>
      <div className="activity-summary">
        <div className="activity-summary-header">
          <strong>Recent milestones</strong>
          <span>{recentMilestones.length ? `${recentMilestones.length} updates` : 'Waiting for work'}</span>
        </div>
        {recentMilestones.length ? (
          recentMilestones.map((entry) => (
            <article key={entry.id} className={`activity-entry activity-${entry.tone}`}>
              <div className="activity-entry-header">
                <strong>{entry.message}</strong>
                <span>{entry.timestamp}</span>
              </div>
              {entry.detail ? <p className="activity-entry-detail">{entry.detail}</p> : null}
              <div className="activity-entry-meta">
                <span>{Math.round(entry.progress * 100)}%</span>
                <span>{entry.source ? sourceLabel(entry.source) : 'Stage update'}</span>
              </div>
            </article>
          ))
        ) : (
          <div className="activity-empty">Milestones for the current conversion will appear here.</div>
        )}
      </div>
      <button type="button" className="log-toggle" onClick={onToggleLog} aria-expanded={logOpen}>
        {logOpen
          ? `Hide raw output (${rawOutputEntries.length} lines)`
          : `Show raw output (${rawOutputEntries.length} lines)`}
      </button>
      {logOpen ? (
        <div className="raw-output-log" role="log" aria-live="polite" aria-relevant="additions text">
          {rawOutputEntries.length ? (
            rawOutputEntries.map((entry) => (
              <div key={entry.id} className="raw-output-line">
                <span>{entry.timestamp}</span>
                <code>{entry.message}</code>
              </div>
            ))
          ) : (
            <div className="activity-empty">
              Raw output is kept minimized by default. Expand it during a conversion to inspect
              pipeline messages.
            </div>
          )}
        </div>
      ) : null}
      <div className="wizard-actions">
        {busy ? (
          <button type="button" className="ghost-button" onClick={onCancel} disabled={cancelRequested}>
            {cancelRequested ? 'Canceling…' : 'Cancel conversion'}
          </button>
        ) : (
          <>
            <button type="button" onClick={onConvertAgain} disabled={!canConvert}>
              Convert again
            </button>
            <button type="button" className="ghost-button" onClick={onAdjustSettings}>
              Change format
            </button>
          </>
        )}
        <button type="button" className="ghost-button" onClick={onRestart} disabled={busy}>
          Start over
        </button>
      </div>
    </div>
  );
}
