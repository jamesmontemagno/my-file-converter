import { useEffect, useRef, useState } from 'react';

type MediaTrimScrubberProps = {
  file: File;
  mediaType: 'audio' | 'video';
  trimStart: string;
  trimEnd: string;
  disabled: boolean;
  onTrimStartChange: (value: string) => void;
  onTrimEndChange: (value: string) => void;
};

function parseSeconds(value: string) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 0;
}

function clamp(value: number, minimum: number, maximum: number) {
  return Math.min(maximum, Math.max(minimum, value));
}

function formatTimestamp(seconds: number) {
  const wholeSeconds = Math.max(0, Math.floor(seconds));
  const hours = Math.floor(wholeSeconds / 3600);
  const minutes = Math.floor((wholeSeconds % 3600) / 60);
  const remainingSeconds = wholeSeconds % 60;
  const time = `${minutes.toString().padStart(2, '0')}:${remainingSeconds.toString().padStart(2, '0')}`;
  return hours ? `${hours}:${time}` : time;
}

function formatInputSeconds(seconds: number) {
  return (Math.round(seconds * 10) / 10).toFixed(1);
}

export function MediaTrimScrubber({
  file,
  mediaType,
  trimStart,
  trimEnd,
  disabled,
  onTrimStartChange,
  onTrimEndChange,
}: MediaTrimScrubberProps) {
  const mediaRef = useRef<HTMLMediaElement | null>(null);
  const [sourceUrl, setSourceUrl] = useState('');
  const [duration, setDuration] = useState(0);
  const [currentTime, setCurrentTime] = useState(0);
  const [isPreviewing, setIsPreviewing] = useState(false);

  useEffect(() => {
    const nextUrl = URL.createObjectURL(file);
    setSourceUrl(nextUrl);
    setDuration(0);
    setCurrentTime(0);
    setIsPreviewing(false);
    return () => URL.revokeObjectURL(nextUrl);
  }, [file]);

  const selectedStart = duration ? clamp(parseSeconds(trimStart), 0, duration) : 0;
  const requestedEnd = parseSeconds(trimEnd);
  const selectedEnd = duration && requestedEnd > selectedStart ? clamp(requestedEnd, selectedStart, duration) : duration;
  const selectionDuration = Math.max(0, selectedEnd - selectedStart);
  const selectedStartPercent = duration ? (selectedStart / duration) * 100 : 0;
  const selectedEndPercent = duration ? (selectedEnd / duration) * 100 : 100;
  const selectionStyle = {
    left: `${selectedStartPercent}%`,
    width: `${Math.max(0, selectedEndPercent - selectedStartPercent)}%`,
  };

  function updateStart(value: number) {
    const nextStart = Math.min(value, Math.max(0, selectedEnd - 0.1));
    onTrimStartChange(nextStart > 0 ? formatInputSeconds(nextStart) : '');
    if (mediaRef.current) mediaRef.current.currentTime = nextStart;
  }

  function updateEnd(value: number) {
    const nextEnd = Math.max(value, Math.min(duration, selectedStart + 0.1));
    onTrimEndChange(nextEnd >= duration - 0.05 ? '' : formatInputSeconds(nextEnd));
  }

  function resetTrim() {
    onTrimStartChange('');
    onTrimEndChange('');
    if (mediaRef.current) mediaRef.current.currentTime = 0;
  }

  async function previewSelection() {
    const media = mediaRef.current;
    if (!media || !duration) return;

    if (isPreviewing) {
      media.pause();
      setIsPreviewing(false);
      return;
    }

    media.currentTime = selectedStart;
    try {
      await media.play();
      setIsPreviewing(true);
    } catch {
      setIsPreviewing(false);
    }
  }

  function handleTimeUpdate() {
    const media = mediaRef.current;
    if (!media) return;
    setCurrentTime(media.currentTime);
    if (isPreviewing && media.currentTime >= selectedEnd) {
      media.pause();
      media.currentTime = selectedStart;
      setIsPreviewing(false);
    }
  }

  const mediaElement =
    mediaType === 'video' ? (
      <video
        ref={(element) => {
          mediaRef.current = element;
        }}
        className="trim-preview"
        controls
        preload="metadata"
        src={sourceUrl}
        onLoadedMetadata={(event) => setDuration(Number.isFinite(event.currentTarget.duration) ? event.currentTarget.duration : 0)}
        onTimeUpdate={handleTimeUpdate}
        onPause={() => setIsPreviewing(false)}
      />
    ) : (
      <audio
        ref={(element) => {
          mediaRef.current = element;
        }}
        className="trim-preview"
        controls
        preload="metadata"
        src={sourceUrl}
        onLoadedMetadata={(event) => setDuration(Number.isFinite(event.currentTarget.duration) ? event.currentTarget.duration : 0)}
        onTimeUpdate={handleTimeUpdate}
        onPause={() => setIsPreviewing(false)}
      />
    );

  return (
    <div className="media-trim-scrubber">
      <div className="media-trim-header">
        <div>
          <span className="meta-label">Trim &amp; preview</span>
          <strong>{duration ? `${formatTimestamp(selectedStart)} – ${formatTimestamp(selectedEnd)}` : 'Reading media duration…'}</strong>
        </div>
        <span className="media-trim-duration">{duration ? `${formatTimestamp(selectionDuration)} selected` : 'Preview unavailable until loaded'}</span>
      </div>
      {mediaElement}
      {duration ? (
        <>
          <div className="trim-range" aria-label="Selected trim range">
            <div className="trim-range-selection" style={selectionStyle} />
            <input
              className="trim-range-input trim-range-start"
              type="range"
              min="0"
              max={duration}
              step="0.1"
              value={selectedStart}
              disabled={disabled}
              aria-label="Trim start"
              aria-valuetext={formatTimestamp(selectedStart)}
              onChange={(event) => updateStart(Number(event.target.value))}
            />
            <input
              className="trim-range-input trim-range-end"
              type="range"
              min="0"
              max={duration}
              step="0.1"
              value={selectedEnd}
              disabled={disabled}
              aria-label="Trim end"
              aria-valuetext={formatTimestamp(selectedEnd)}
              onChange={(event) => updateEnd(Number(event.target.value))}
            />
          </div>
          <div className="media-trim-labels" aria-hidden="true">
            <span>0:00</span>
            <span>{formatTimestamp(duration)}</span>
          </div>
          <div className="media-trim-actions">
            <button type="button" className="ghost-button" disabled={disabled} onClick={() => void previewSelection()}>
              {isPreviewing ? 'Pause selection' : 'Preview selection'}
            </button>
            <button type="button" className="text-button" disabled={disabled} onClick={resetTrim}>
              Reset trim
            </button>
            <span>Playhead: {formatTimestamp(currentTime)}</span>
          </div>
        </>
      ) : (
        <p className="muted">If this preview cannot load in the browser, use the precise trim fields below.</p>
      )}
    </div>
  );
}
