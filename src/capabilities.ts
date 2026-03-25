export type MediaKind = 'image' | 'audio' | 'video' | 'unknown';

export type CapabilityReport = {
  mediaRecorder: boolean;
  mp3Encoder: boolean;
  webCodecs: {
    videoEncoder: boolean;
    audioEncoder: boolean;
    imageDecoder: boolean;
  };
  fileSystemAccess: {
    openPicker: boolean;
    savePicker: boolean;
  };
  workers: boolean;
  imageFormats: Record<string, boolean>;
  mediaRecorderTypes: Record<string, boolean>;
};

export function detectCapabilities(): CapabilityReport {
  const hasMp3EncoderContext =
    typeof window.AudioContext !== 'undefined' ||
    typeof (window as Window & { webkitAudioContext?: unknown }).webkitAudioContext !== 'undefined';

  const windowWithPickers = window as Window & {
    showOpenFilePicker?: unknown;
    showSaveFilePicker?: unknown;
  };

  const canMediaRecorder = typeof window.MediaRecorder !== 'undefined';
  const mediaRecorderTypes = canMediaRecorder
    ? [
        'video/webm;codecs=vp8,opus',
        'video/webm;codecs=vp9,opus',
        'video/mp4;codecs=avc1.42E01E,mp4a.40.2',
        'audio/webm;codecs=opus',
        'audio/ogg;codecs=opus',
        'audio/mp4',
      ].reduce<Record<string, boolean>>((acc, type) => {
        acc[type] = MediaRecorder.isTypeSupported(type);
        return acc;
      }, {})
    : {};

  const c = document.createElement('canvas');
  const imageFormats = {
    'image/png': c.toDataURL('image/png').startsWith('data:image/png'),
    'image/jpeg': c.toDataURL('image/jpeg').startsWith('data:image/jpeg'),
    'image/webp': c.toDataURL('image/webp').startsWith('data:image/webp'),
    'image/avif': c.toDataURL('image/avif').startsWith('data:image/avif'),
  };

  return {
    mediaRecorder: canMediaRecorder,
    mp3Encoder: hasMp3EncoderContext,
    webCodecs: {
      videoEncoder: typeof window.VideoEncoder !== 'undefined',
      audioEncoder: typeof window.AudioEncoder !== 'undefined',
      imageDecoder: typeof window.ImageDecoder !== 'undefined',
    },
    fileSystemAccess: {
      openPicker: typeof windowWithPickers.showOpenFilePicker === 'function',
      savePicker: typeof windowWithPickers.showSaveFilePicker === 'function',
    },
    workers: typeof window.Worker !== 'undefined',
    imageFormats,
    mediaRecorderTypes,
  };
}

export function classifyMediaType(file: File | null | undefined): MediaKind {
  if (!file?.type) return 'unknown';
  if (file.type.startsWith('image/')) return 'image';
  if (file.type.startsWith('audio/')) return 'audio';
  if (file.type.startsWith('video/')) return 'video';
  return 'unknown';
}

export function targetFormatsFor(mediaType: MediaKind) {
  if (mediaType === 'image') {
    return [
      { value: 'image/png', label: 'PNG (.png)' },
      { value: 'image/jpeg', label: 'JPEG (.jpg)' },
      { value: 'image/webp', label: 'WebP (.webp)' },
      { value: 'image/avif', label: 'AVIF (.avif)' },
    ];
  }

  if (mediaType === 'audio') {
    return [
      { value: 'audio/webm;codecs=opus', label: 'WebM Opus (.webm)' },
      { value: 'audio/ogg;codecs=opus', label: 'Ogg Opus (.ogg)' },
      { value: 'audio/mp4', label: 'MP4 Audio (.m4a)' },
      { value: 'audio/mpeg', label: 'MP3 (.mp3)' },
      { value: 'audio/wav', label: 'WAV PCM (.wav)' },
    ];
  }

  if (mediaType === 'video') {
    return [
      { value: 'video/webm;codecs=vp8,opus', label: 'WebM VP8+Opus (.webm)' },
      { value: 'video/webm;codecs=vp9,opus', label: 'WebM VP9+Opus (.webm)' },
      { value: 'video/mp4;codecs=avc1.42E01E,mp4a.40.2', label: 'MP4 H.264+AAC (.mp4)' },
      { value: 'audio/mpeg', label: 'Extract audio as MP3 (.mp3)' },
      { value: 'audio/wav', label: 'Extract audio as WAV (.wav)' },
    ];
  }

  return [];
}

export function isTargetMimeSupported(
  targetMime: string,
  capabilities: CapabilityReport | null,
) {
  if (!capabilities) return false;

  if (targetMime === 'audio/mpeg' || targetMime === 'audio/wav') {
    return capabilities.mp3Encoder === true;
  }

  if (targetMime.startsWith('image/')) {
    return capabilities.imageFormats[targetMime] === true;
  }

  if (targetMime.startsWith('audio/') || targetMime.startsWith('video/')) {
    return capabilities.mediaRecorder === true && capabilities.mediaRecorderTypes[targetMime] === true;
  }

  return false;
}
