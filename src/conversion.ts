import { classifyMediaType } from './capabilities';
import type { ImageConversionOptions } from './conversion-options';

export const CONVERSION_CANCELED_MESSAGE = 'Conversion canceled.';

export type ConversionResult = {
  blob: Blob;
  outputName: string;
  route: string;
};

export type ConversionActivity = {
  progress: number;
  message: string;
  detail?: string;
  rawOutput?: string;
  source?: 'native' | 'encoder';
};

export function createConversionAbortError() {
  return new DOMException(CONVERSION_CANCELED_MESSAGE, 'AbortError');
}

export function isConversionAbortError(error: unknown) {
  return (
    (error instanceof DOMException && error.name === 'AbortError') ||
    (error instanceof Error &&
      (error.name === 'AbortError' || error.message === CONVERSION_CANCELED_MESSAGE))
  );
}

function throwIfConversionAborted(signal?: AbortSignal) {
  if (signal?.aborted) {
    throw createConversionAbortError();
  }
}

function raceWithAbort<T>(promise: Promise<T>, signal?: AbortSignal, onAbort?: () => void) {
  if (!signal) return promise;
  if (signal.aborted) {
    onAbort?.();
    return Promise.reject(createConversionAbortError());
  }

  return new Promise<T>((resolve, reject) => {
    const handleAbort = () => {
      onAbort?.();
      reject(createConversionAbortError());
    };

    signal.addEventListener('abort', handleAbort, { once: true });
    promise.then(
      (value) => {
        signal.removeEventListener('abort', handleAbort);
        resolve(value);
      },
      (error) => {
        signal.removeEventListener('abort', handleAbort);
        reject(error);
      },
    );
  });
}

function resolveImageSize(sourceWidth: number, sourceHeight: number, options?: ImageConversionOptions) {
  const targetWidth = options?.width ?? null;
  const targetHeight = options?.height ?? null;

  if (!targetWidth && !targetHeight) {
    return { width: sourceWidth, height: sourceHeight };
  }

  if (options?.keepAspectRatio !== false) {
    if (targetWidth && targetHeight) {
      const scale = Math.min(targetWidth / sourceWidth, targetHeight / sourceHeight);
      return {
        width: Math.max(1, Math.round(sourceWidth * scale)),
        height: Math.max(1, Math.round(sourceHeight * scale)),
      };
    }

    if (targetWidth) {
      return {
        width: targetWidth,
        height: Math.max(1, Math.round(sourceHeight * (targetWidth / sourceWidth))),
      };
    }

    return {
      width: Math.max(1, Math.round(sourceWidth * ((targetHeight ?? sourceHeight) / sourceHeight))),
      height: targetHeight ?? sourceHeight,
    };
  }

  return {
    width: targetWidth ?? sourceWidth,
    height: targetHeight ?? sourceHeight,
  };
}

function mimeToExt(mime: string) {
  if (mime.includes('audio/wav')) return 'wav';
  if (mime.includes('audio/mpeg')) return 'mp3';
  if (mime.includes('png')) return 'png';
  if (mime.includes('jpeg')) return 'jpg';
  if (mime.includes('webp')) return 'webp';
  if (mime.includes('avif')) return 'avif';
  if (mime.includes('video/mp4')) return 'mp4';
  if (mime.includes('video/webm')) return 'webm';
  if (mime.includes('audio/ogg')) return 'ogg';
  if (mime.includes('audio/webm')) return 'webm';
  if (mime.includes('audio/mp4')) return 'm4a';
  return 'bin';
}

function replaceExtension(name: string, ext: string) {
  const idx = name.lastIndexOf('.');
  const base = idx > 0 ? name.slice(0, idx) : name;
  return `${base}.${ext}`;
}

function isHeicLikeFile(file: File) {
  const type = file.type.toLowerCase();
  if (type === 'image/heic' || type === 'image/heif' || type === 'image/heic-sequence' || type === 'image/heif-sequence') {
    return true;
  }

  const lowerName = file.name.toLowerCase();
  return lowerName.endsWith('.heic') || lowerName.endsWith('.heif');
}

async function prepareImageDecodeBlob(file: File, onProgress?: (activity: ConversionActivity) => void) {
  if (!isHeicLikeFile(file)) return file;

  onProgress?.({
    progress: 0.16,
    message: 'Decoding HEIC/HEIF image',
    detail: 'Converting source image to a browser-decodable bitmap.',
    source: 'encoder',
  });

  const heic2anyModule = await import('heic2any');
  const converted = await heic2anyModule.default({
    blob: file,
    toType: 'image/png',
  });

  return Array.isArray(converted) ? converted[0] : converted;
}

async function decodeMediaAudioBuffer(args: {
  file: File;
  signal?: AbortSignal;
  onProgress?: (activity: ConversionActivity) => void;
}) {
  const { file, signal, onProgress } = args;
  const AudioContextCtor = getAudioContextCtor();
  if (!AudioContextCtor) {
    throw new Error('Web Audio API is not supported in this browser.');
  }

  const sourceBytes = await raceWithAbort(file.arrayBuffer(), signal);
  throwIfConversionAborted(signal);

  const audioContext = new AudioContextCtor();
  try {
    onProgress?.({
      progress: 0.22,
      message: 'Decoding source audio',
      detail: 'Using Web Audio to decode media samples.',
      source: 'encoder',
    });

    return await raceWithAbort(audioContext.decodeAudioData(sourceBytes.slice(0)), signal, () => {
      void audioContext.close();
    });
  } finally {
    await audioContext.close();
  }
}

function createWavBlob(audioBuffer: AudioBuffer) {
  const channels = Math.min(2, audioBuffer.numberOfChannels);
  const sampleRate = audioBuffer.sampleRate;
  const samples = audioBuffer.length;
  const bytesPerSample = 2;
  const blockAlign = channels * bytesPerSample;
  const byteRate = sampleRate * blockAlign;
  const dataSize = samples * blockAlign;
  const buffer = new ArrayBuffer(44 + dataSize);
  const view = new DataView(buffer);

  const writeAscii = (offset: number, value: string) => {
    for (let index = 0; index < value.length; index += 1) {
      view.setUint8(offset + index, value.charCodeAt(index));
    }
  };

  writeAscii(0, 'RIFF');
  view.setUint32(4, 36 + dataSize, true);
  writeAscii(8, 'WAVE');
  writeAscii(12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, channels, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, byteRate, true);
  view.setUint16(32, blockAlign, true);
  view.setUint16(34, 16, true);
  writeAscii(36, 'data');
  view.setUint32(40, dataSize, true);

  let offset = 44;
  const left = audioBuffer.getChannelData(0);
  const right = channels > 1 ? audioBuffer.getChannelData(1) : null;

  for (let sample = 0; sample < samples; sample += 1) {
    const l = Math.max(-1, Math.min(1, left[sample]));
    view.setInt16(offset, l < 0 ? l * 0x8000 : l * 0x7fff, true);
    offset += 2;

    if (right) {
      const r = Math.max(-1, Math.min(1, right[sample]));
      view.setInt16(offset, r < 0 ? r * 0x8000 : r * 0x7fff, true);
      offset += 2;
    }
  }

  return new Blob([buffer], { type: 'audio/wav' });
}

export async function convertImage(args: {
  file: File;
  targetMime: string;
  quality: number;
  imageOptions?: ImageConversionOptions;
  onProgress?: (activity: ConversionActivity) => void;
  signal?: AbortSignal;
}): Promise<ConversionResult> {
  const { file, targetMime, quality, imageOptions, onProgress, signal } = args;
  throwIfConversionAborted(signal);
  onProgress?.({
    progress: 0.1,
    message: 'Decoding image',
    detail: `Reading ${file.type || 'source image'} into the browser canvas.`,
    source: 'native',
  });
  const decodeBlob = await raceWithAbort(prepareImageDecodeBlob(file, onProgress), signal);
  const bitmap = await raceWithAbort(createImageBitmap(decodeBlob), signal);
  const nextSize = resolveImageSize(bitmap.width, bitmap.height, imageOptions);
  const canvas = document.createElement('canvas');
  canvas.width = nextSize.width;
  canvas.height = nextSize.height;
  const ctx = canvas.getContext('2d');
  if (!ctx) {
    bitmap.close();
    throw new Error('Could not create 2D canvas context.');
  }

  if (nextSize.width !== bitmap.width || nextSize.height !== bitmap.height) {
    onProgress?.({
      progress: 0.35,
      message: `Resizing image to ${nextSize.width}×${nextSize.height}`,
      detail: 'Scaling the decoded image before export.',
      source: 'native',
    });
  }

  ctx.drawImage(bitmap, 0, 0, nextSize.width, nextSize.height);
  bitmap.close();
  throwIfConversionAborted(signal);

  onProgress?.({
    progress: 0.6,
    message: 'Encoding image',
    detail: `Exporting the canvas as ${targetMime}.`,
    source: 'native',
  });
  const blob = await new Promise<Blob>((resolve, reject) => {
    let settled = false;
    const cleanup = () => signal?.removeEventListener('abort', handleAbort);
    const settle = (callback: () => void) => {
      if (settled) return;
      settled = true;
      cleanup();
      callback();
    };
    const handleAbort = () => settle(() => reject(createConversionAbortError()));
    signal?.addEventListener('abort', handleAbort, { once: true });
    if (signal?.aborted) {
      handleAbort();
      return;
    }
    canvas.toBlob(
      (result) => {
        if (!result) {
          settle(() => reject(new Error(`Unable to encode as ${targetMime}.`)));
          return;
        }
        settle(() => resolve(result));
      },
      targetMime,
      quality,
    );
  });

  return {
    blob,
    outputName: replaceExtension(file.name, mimeToExt(targetMime)),
    route: 'native-image',
  };
}

type CaptureMediaElement = HTMLAudioElement | HTMLVideoElement;
type CaptureCapableElement = CaptureMediaElement & { captureStream?: () => MediaStream };

async function createMediaElement(file: File, mediaType: 'audio' | 'video'): Promise<CaptureMediaElement> {
  const el = document.createElement(mediaType === 'audio' ? 'audio' : 'video');
  el.preload = 'auto';
  el.muted = true;
  (el as HTMLVideoElement).playsInline = true;
  el.src = URL.createObjectURL(file);
  await el.play().catch(() => undefined);
  el.pause();
  el.currentTime = 0;
  return el;
}

function mediaRecorderSupported(type: string) {
  return typeof MediaRecorder !== 'undefined' && MediaRecorder.isTypeSupported(type);
}

function getAudioContextCtor() {
  return (window.AudioContext ||
    (window as Window & { webkitAudioContext?: typeof AudioContext }).webkitAudioContext) as
    | typeof AudioContext
    | undefined;
}

function floatToInt16(input: Float32Array) {
  const output = new Int16Array(input.length);
  for (let index = 0; index < input.length; index += 1) {
    const sample = Math.max(-1, Math.min(1, input[index]));
    output[index] = sample < 0 ? sample * 0x8000 : sample * 0x7fff;
  }
  return output;
}

export async function convertAudioToMp3(args: {
  file: File;
  onProgress?: (activity: ConversionActivity) => void;
  signal?: AbortSignal;
}): Promise<ConversionResult> {
  const { file, onProgress, signal } = args;
  const mediaType = classifyMediaType(file);
  if (mediaType !== 'audio' && mediaType !== 'video') {
    throw new Error('MP3 encoding currently supports audio or video input.');
  }

  const AudioContextCtor = getAudioContextCtor();
  if (!AudioContextCtor) {
    throw new Error('Web Audio API is not supported in this browser.');
  }

  throwIfConversionAborted(signal);
  onProgress?.({
    progress: 0.08,
    message: 'Preparing MP3 encoder',
    detail: 'Loading local audio encoder and reading input file.',
    source: 'encoder',
  });

  const lamejsModule = await import('lamejs');
  const Mp3Encoder = (lamejsModule as { Mp3Encoder: new (...args: number[]) => any }).Mp3Encoder;

  const audioBuffer = await decodeMediaAudioBuffer({ file, signal, onProgress });

  throwIfConversionAborted(signal);

  const channels = Math.min(2, audioBuffer.numberOfChannels);
  const sampleRate = audioBuffer.sampleRate;
  const kbps = 128;
  const encoder = new Mp3Encoder(channels, sampleRate, kbps);
  const chunkSize = 1152;
  const totalSamples = audioBuffer.length;
  const totalChunks = Math.max(1, Math.ceil(totalSamples / chunkSize));

  const left = floatToInt16(audioBuffer.getChannelData(0));
  const right = channels > 1 ? floatToInt16(audioBuffer.getChannelData(1)) : null;
  const mp3Chunks: ArrayBuffer[] = [];

  onProgress?.({
    progress: 0.35,
    message: 'Encoding MP3 output',
    detail: 'Converting decoded samples into MP3 frames.',
    source: 'encoder',
  });

  for (let chunkIndex = 0; chunkIndex < totalChunks; chunkIndex += 1) {
    throwIfConversionAborted(signal);
    const start = chunkIndex * chunkSize;
    const end = Math.min(start + chunkSize, totalSamples);
    const leftChunk = left.subarray(start, end);
    const frame = right
      ? encoder.encodeBuffer(leftChunk, right.subarray(start, end))
      : encoder.encodeBuffer(leftChunk);
    if (frame.length) {
      mp3Chunks.push(Uint8Array.from(frame).buffer);
    }

    if (chunkIndex % 12 === 0 || chunkIndex === totalChunks - 1) {
      const completion = (chunkIndex + 1) / totalChunks;
      onProgress?.({
        progress: 0.35 + completion * 0.58,
        message: 'Encoding MP3 output',
        detail: `Encoded ${Math.round(completion * 100)}% of audio samples.`,
        source: 'encoder',
      });
    }
  }

  const flush = encoder.flush();
  if (flush.length) {
    mp3Chunks.push(Uint8Array.from(flush).buffer);
  }

  return {
    blob: new Blob(mp3Chunks, { type: 'audio/mpeg' }),
    outputName: replaceExtension(file.name, mimeToExt('audio/mpeg')),
    route: 'encoder-mp3',
  };
}

export async function convertAudioToWav(args: {
  file: File;
  onProgress?: (activity: ConversionActivity) => void;
  signal?: AbortSignal;
}): Promise<ConversionResult> {
  const { file, onProgress, signal } = args;
  const mediaType = classifyMediaType(file);
  if (mediaType !== 'audio' && mediaType !== 'video') {
    throw new Error('WAV encoding currently supports audio or video input.');
  }

  throwIfConversionAborted(signal);
  onProgress?.({
    progress: 0.08,
    message: 'Preparing WAV export',
    detail: 'Reading media stream for lossless PCM packaging.',
    source: 'encoder',
  });

  const audioBuffer = await decodeMediaAudioBuffer({ file, signal, onProgress });
  throwIfConversionAborted(signal);

  onProgress?.({
    progress: 0.7,
    message: 'Packaging WAV output',
    detail: 'Writing RIFF/WAVE container from decoded samples.',
    source: 'encoder',
  });

  return {
    blob: createWavBlob(audioBuffer),
    outputName: replaceExtension(file.name, mimeToExt('audio/wav')),
    route: 'encoder-wav',
  };
}

export async function convertViaMediaRecorder(args: {
  file: File;
  targetMime: string;
  onProgress?: (activity: ConversionActivity) => void;
  signal?: AbortSignal;
}): Promise<ConversionResult> {
  const { file, targetMime, onProgress, signal } = args;
  const mediaType = classifyMediaType(file);
  if (mediaType !== 'audio' && mediaType !== 'video') {
    throw new Error('MediaRecorder conversion supports only audio/video input.');
  }
  if (!mediaRecorderSupported(targetMime)) {
    throw new Error(`MediaRecorder does not support target MIME type: ${targetMime}`);
  }

  throwIfConversionAborted(signal);
  onProgress?.({
    progress: 0.1,
    message: 'Preparing media stream',
    detail: 'Loading the source media into a browser playback element.',
    source: 'native',
  });
  const element = await raceWithAbort(createMediaElement(file, mediaType), signal);
  const captureStream = (element as CaptureCapableElement).captureStream;
  if (!captureStream) {
    URL.revokeObjectURL(element.src);
    throw new Error('captureStream is not supported in this browser.');
  }

  const stream = captureStream.call(element as CaptureCapableElement);
  const chunks: BlobPart[] = [];
  const recorder = new MediaRecorder(stream, { mimeType: targetMime });

  const finished = new Promise<void>((resolve, reject) => {
    recorder.ondataavailable = (event) => {
      if (event.data.size > 0) chunks.push(event.data);
    };
    recorder.onerror = () => reject(new Error('MediaRecorder failed.'));
    recorder.onstop = () => resolve();
  });

  recorder.start(500);
  onProgress?.({
    progress: 0.3,
    message: 'Recording converted stream',
    detail: `Capturing ${mediaType} output with MediaRecorder.`,
    source: 'native',
  });
  const progressInterval = window.setInterval(() => {
    const duration = Number.isFinite(element.duration) ? element.duration : 0;
    if (duration > 0) {
      const completion = Math.min(1, element.currentTime / duration);
      onProgress?.({
        progress: 0.3 + completion * 0.65,
        message: 'Recording converted stream',
        detail: `Captured ${Math.round(completion * 100)}% of the source duration.`,
        source: 'native',
      });
    }
  }, 250);

  try {
    await raceWithAbort(
      new Promise<void>((resolve, reject) => {
        element.onended = () => resolve();
        element.onerror = () => reject(new Error('Playback failed for source media.'));
        element.play().catch(reject);
      }),
      signal,
      () => {
        element.pause();
        if (recorder.state !== 'inactive') {
          recorder.stop();
        }
      },
    );
    throwIfConversionAborted(signal);

    onProgress?.({
      progress: 0.96,
      message: 'Finalizing recording',
      detail: 'Stopping the recorder and assembling the output file.',
      source: 'native',
    });
    recorder.stop();
    await raceWithAbort(finished, signal);
    throwIfConversionAborted(signal);
  } finally {
    if (recorder.state !== 'inactive') {
      recorder.stop();
    }
    window.clearInterval(progressInterval);
    stream.getTracks().forEach((track) => track.stop());
    URL.revokeObjectURL(element.src);
  }

  return {
    blob: new Blob(chunks, { type: targetMime }),
    outputName: replaceExtension(file.name, mimeToExt(targetMime)),
    route: 'native-mediarecorder',
  };
}

export function supportsNativeRoute(file: File, targetMime: string) {
  const mediaType = classifyMediaType(file);
  if (mediaType === 'image') return true;
  if (mediaType === 'audio' || mediaType === 'video') return mediaRecorderSupported(targetMime);
  return false;
}
