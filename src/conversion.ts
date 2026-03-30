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
  if (mime.includes('gif')) return 'gif';
  if (mime.includes('bmp')) return 'bmp';
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

  onProgress?.({
    progress: 0.14,
    message: 'Reading source file',
    detail: `Loading ${(file.size / 1024 / 1024).toFixed(1)} MB into memory.`,
    source: 'encoder',
  });

  const sourceBytes = await raceWithAbort(file.arrayBuffer(), signal);
  throwIfConversionAborted(signal);

  const audioContext = new AudioContextCtor();
  try {
    onProgress?.({
      progress: 0.22,
      message: 'Decoding source audio',
      detail: 'Using Web Audio to decode media samples. This may take a moment for longer files.',
      source: 'encoder',
    });

    const decoded = await raceWithAbort(audioContext.decodeAudioData(sourceBytes.slice(0)), signal, () => {
      void audioContext.close();
    });

    onProgress?.({
      progress: 0.30,
      message: 'Audio decoded successfully',
      detail: `${decoded.numberOfChannels} channel(s), ${decoded.sampleRate} Hz, ${(decoded.duration).toFixed(1)}s duration.`,
      source: 'encoder',
    });

    return decoded;
  } finally {
    await audioContext.close();
  }
}

function createWavBlob(audioBuffer: AudioBuffer, channelMode: AudioChannelMode = 'auto') {
  const channels = resolveChannelCount(audioBuffer, channelMode);
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
  const left = getChannelData(audioBuffer, 0, channelMode);
  const right = channels > 1 ? getChannelData(audioBuffer, 1, channelMode) : null;

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

  onProgress?.({
    progress: 0.2,
    message: 'Creating bitmap',
    detail: 'Decoding image pixels for processing.',
    source: 'native',
  });

  const bitmap = await raceWithAbort(createImageBitmap(decodeBlob), signal);

  onProgress?.({
    progress: 0.3,
    message: 'Image decoded',
    detail: `Source dimensions: ${bitmap.width}×${bitmap.height}.`,
    source: 'native',
  });

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

  onProgress?.({
    progress: 0.95,
    message: 'Image file ready',
    detail: `Output size: ${(blob.size / 1024).toFixed(0)} KB.`,
    source: 'native',
  });

  return {
    blob,
    outputName: replaceExtension(file.name, mimeToExt(targetMime)),
    route: 'native-image',
  };
}

export async function convertImageToGif(args: {
  file: File;
  imageOptions?: ImageConversionOptions;
  onProgress?: (activity: ConversionActivity) => void;
  signal?: AbortSignal;
}): Promise<ConversionResult> {
  const { file, imageOptions, onProgress, signal } = args;
  throwIfConversionAborted(signal);

  onProgress?.({
    progress: 0.1,
    message: 'Decoding image for GIF',
    detail: 'Reading source image pixels.',
    source: 'encoder',
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
  ctx.drawImage(bitmap, 0, 0, nextSize.width, nextSize.height);
  bitmap.close();
  throwIfConversionAborted(signal);

  onProgress?.({
    progress: 0.3,
    message: 'Quantizing palette',
    detail: 'Reducing colors for GIF format (max 256 colors).',
    source: 'encoder',
  });

  const { GIFEncoder, quantize, applyPalette } = await import('gifenc');
  const rgba = ctx.getImageData(0, 0, nextSize.width, nextSize.height).data;

  const palette = quantize(rgba, 256);
  throwIfConversionAborted(signal);

  onProgress?.({
    progress: 0.6,
    message: 'Encoding GIF',
    detail: 'Mapping pixels to palette and writing GIF data.',
    source: 'encoder',
  });

  const index = applyPalette(rgba, palette);
  const gif = GIFEncoder();
  gif.writeFrame(index, nextSize.width, nextSize.height, { palette });
  gif.finish();

  const gifBytes = gif.bytes();
  const gifBlob = new Blob([new Uint8Array(gifBytes)], { type: 'image/gif' });

  onProgress?.({
    progress: 0.95,
    message: 'GIF file ready',
    detail: `Output size: ${(gifBlob.size / 1024).toFixed(0)} KB.`,
    source: 'encoder',
  });

  return {
    blob: gifBlob,
    outputName: replaceExtension(file.name, 'gif'),
    route: 'encoder-gif',
  };
}

export async function convertImageToBmp(args: {
  file: File;
  imageOptions?: ImageConversionOptions;
  onProgress?: (activity: ConversionActivity) => void;
  signal?: AbortSignal;
}): Promise<ConversionResult> {
  const { file, imageOptions, onProgress, signal } = args;
  throwIfConversionAborted(signal);

  onProgress?.({
    progress: 0.1,
    message: 'Decoding image for BMP',
    detail: 'Reading source image pixels.',
    source: 'encoder',
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
  ctx.drawImage(bitmap, 0, 0, nextSize.width, nextSize.height);
  bitmap.close();
  throwIfConversionAborted(signal);

  onProgress?.({
    progress: 0.4,
    message: 'Encoding BMP',
    detail: 'Writing raw pixel data into BMP container.',
    source: 'encoder',
  });

  const imageData = ctx.getImageData(0, 0, nextSize.width, nextSize.height);
  const { width, height } = nextSize;

  // BMP rows must be padded to 4-byte boundary
  const rowSize = Math.ceil((width * 3) / 4) * 4;
  const pixelDataSize = rowSize * height;
  const fileSize = 54 + pixelDataSize;
  const buffer = new ArrayBuffer(fileSize);
  const view = new DataView(buffer);

  // BMP file header (14 bytes)
  view.setUint8(0, 0x42); // 'B'
  view.setUint8(1, 0x4d); // 'M'
  view.setUint32(2, fileSize, true);
  view.setUint32(6, 0, true); // reserved
  view.setUint32(10, 54, true); // pixel data offset

  // DIB header (BITMAPINFOHEADER, 40 bytes)
  view.setUint32(14, 40, true); // header size
  view.setInt32(18, width, true);
  view.setInt32(22, height, true); // positive = bottom-up
  view.setUint16(26, 1, true); // color planes
  view.setUint16(28, 24, true); // bits per pixel (24-bit RGB)
  view.setUint32(30, 0, true); // no compression
  view.setUint32(34, pixelDataSize, true);
  view.setUint32(38, 2835, true); // horizontal resolution (72 DPI)
  view.setUint32(42, 2835, true); // vertical resolution
  view.setUint32(46, 0, true); // colors in palette
  view.setUint32(50, 0, true); // important colors

  // Pixel data (bottom-up, BGR order)
  const pixels = imageData.data;
  for (let y = 0; y < height; y++) {
    const srcRow = height - 1 - y; // BMP is bottom-up
    for (let x = 0; x < width; x++) {
      const srcIdx = (srcRow * width + x) * 4;
      const dstIdx = 54 + y * rowSize + x * 3;
      view.setUint8(dstIdx, pixels[srcIdx + 2]);     // B
      view.setUint8(dstIdx + 1, pixels[srcIdx + 1]); // G
      view.setUint8(dstIdx + 2, pixels[srcIdx]);     // R
    }
  }

  const bmpBlob = new Blob([buffer], { type: 'image/bmp' });

  onProgress?.({
    progress: 0.95,
    message: 'BMP file ready',
    detail: `Output size: ${(bmpBlob.size / 1024).toFixed(0)} KB.`,
    source: 'encoder',
  });

  return {
    blob: bmpBlob,
    outputName: replaceExtension(file.name, 'bmp'),
    route: 'encoder-bmp',
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

export type AudioChannelMode = 'auto' | 'mono' | 'stereo';

function trimAudioBuffer(buffer: AudioBuffer, trimStart: number, trimEnd: number): AudioBuffer {
  const sampleRate = buffer.sampleRate;
  const startSample = Math.max(0, Math.floor(trimStart * sampleRate));
  const endSample = trimEnd > 0
    ? Math.min(buffer.length, Math.floor(trimEnd * sampleRate))
    : buffer.length;

  if (startSample === 0 && endSample === buffer.length) return buffer;

  const length = Math.max(1, endSample - startSample);
  const ctx = new OfflineAudioContext(buffer.numberOfChannels, length, sampleRate);
  const trimmed = ctx.createBuffer(buffer.numberOfChannels, length, sampleRate);

  for (let ch = 0; ch < buffer.numberOfChannels; ch++) {
    const src = buffer.getChannelData(ch);
    trimmed.getChannelData(ch).set(src.subarray(startSample, endSample));
  }

  return trimmed;
}

function resolveChannelCount(buffer: AudioBuffer, mode: AudioChannelMode): number {
  if (mode === 'mono') return 1;
  if (mode === 'stereo') return Math.min(2, buffer.numberOfChannels);
  return Math.min(2, buffer.numberOfChannels);
}

function getChannelData(buffer: AudioBuffer, channelIndex: number, channelMode: AudioChannelMode): Float32Array {
  if (channelMode === 'mono' && buffer.numberOfChannels > 1) {
    // Mix down to mono
    const mixed = new Float32Array(buffer.length);
    const numChannels = buffer.numberOfChannels;
    for (let ch = 0; ch < numChannels; ch++) {
      const data = buffer.getChannelData(ch);
      for (let i = 0; i < buffer.length; i++) {
        mixed[i] += data[i] / numChannels;
      }
    }
    return mixed;
  }
  return buffer.getChannelData(channelIndex);
}

export async function convertAudioToMp3(args: {
  file: File;
  channelMode?: AudioChannelMode;
  trimStart?: number;
  trimEnd?: number;
  onProgress?: (activity: ConversionActivity) => void;
  signal?: AbortSignal;
}): Promise<ConversionResult> {
  const { file, channelMode = 'auto', trimStart = 0, trimEnd = 0, onProgress, signal } = args;
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

  const lameCoreModule = await import('lamejs/src/js/Lame.js');
  (globalThis as { Lame?: unknown }).Lame =
    (lameCoreModule as { default?: unknown }).default ?? lameCoreModule;

  const mpegModeModule = await import('lamejs/src/js/MPEGMode.js');
  (globalThis as { MPEGMode?: unknown }).MPEGMode =
    (mpegModeModule as { default?: unknown }).default ?? mpegModeModule;

  const bitStreamModule = await import('lamejs/src/js/BitStream.js');
  (globalThis as { BitStream?: unknown }).BitStream =
    (bitStreamModule as { default?: unknown }).default ?? bitStreamModule;

  const lamejsModule = await import('lamejs');
  const Mp3Encoder = (lamejsModule as { Mp3Encoder: new (...args: number[]) => any }).Mp3Encoder;

  onProgress?.({
    progress: 0.12,
    message: 'MP3 encoder loaded',
    detail: 'Encoder modules ready. Now reading and decoding source audio.',
    source: 'encoder',
  });

  const rawAudioBuffer = await decodeMediaAudioBuffer({ file, signal, onProgress });

  throwIfConversionAborted(signal);

  const audioBuffer = (trimStart > 0 || trimEnd > 0)
    ? trimAudioBuffer(rawAudioBuffer, trimStart, trimEnd)
    : rawAudioBuffer;

  const channels = resolveChannelCount(audioBuffer, channelMode);
  const sampleRate = audioBuffer.sampleRate;
  const kbps = 128;
  const encoder = new Mp3Encoder(channels, sampleRate, kbps);
  const chunkSize = 1152;
  const totalSamples = audioBuffer.length;
  const totalChunks = Math.max(1, Math.ceil(totalSamples / chunkSize));

  const left = floatToInt16(getChannelData(audioBuffer, 0, channelMode));
  const right = channels > 1 ? floatToInt16(getChannelData(audioBuffer, 1, channelMode)) : null;
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
      // Yield to the browser event loop so the UI can repaint and stay responsive
      await new Promise<void>((resolve) => setTimeout(resolve, 0));
    }
  }

  onProgress?.({
    progress: 0.94,
    message: 'Finalizing MP3',
    detail: 'Flushing encoder and assembling output file.',
    source: 'encoder',
  });

  const flush = encoder.flush();
  if (flush.length) {
    mp3Chunks.push(Uint8Array.from(flush).buffer);
  }

  const mp3Blob = new Blob(mp3Chunks, { type: 'audio/mpeg' });

  onProgress?.({
    progress: 0.98,
    message: 'MP3 file ready',
    detail: `Output size: ${(mp3Blob.size / 1024).toFixed(0)} KB.`,
    source: 'encoder',
  });

  return {
    blob: mp3Blob,
    outputName: replaceExtension(file.name, mimeToExt('audio/mpeg')),
    route: 'encoder-mp3',
  };
}

export async function convertAudioToWav(args: {
  file: File;
  channelMode?: AudioChannelMode;
  trimStart?: number;
  trimEnd?: number;
  onProgress?: (activity: ConversionActivity) => void;
  signal?: AbortSignal;
}): Promise<ConversionResult> {
  const { file, channelMode = 'auto', trimStart = 0, trimEnd = 0, onProgress, signal } = args;
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

  const rawAudioBuffer = await decodeMediaAudioBuffer({ file, signal, onProgress });
  throwIfConversionAborted(signal);

  const audioBuffer = (trimStart > 0 || trimEnd > 0)
    ? trimAudioBuffer(rawAudioBuffer, trimStart, trimEnd)
    : rawAudioBuffer;

  onProgress?.({
    progress: 0.7,
    message: 'Packaging WAV output',
    detail: 'Writing RIFF/WAVE container from decoded samples.',
    source: 'encoder',
  });

  const wavBlob = createWavBlob(audioBuffer, channelMode);

  onProgress?.({
    progress: 0.98,
    message: 'WAV file ready',
    detail: `Output size: ${(wavBlob.size / 1024).toFixed(0)} KB.`,
    source: 'encoder',
  });

  return {
    blob: wavBlob,
    outputName: replaceExtension(file.name, mimeToExt('audio/wav')),
    route: 'encoder-wav',
  };
}

export async function convertViaMediaRecorder(args: {
  file: File;
  targetMime: string;
  trimStart?: number;
  trimEnd?: number;
  onProgress?: (activity: ConversionActivity) => void;
  signal?: AbortSignal;
}): Promise<ConversionResult> {
  const { file, targetMime, trimStart = 0, trimEnd = 0, onProgress, signal } = args;
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

  // Apply trim: seek to start position before recording
  if (trimStart > 0) {
    element.currentTime = trimStart;
    await new Promise<void>((resolve) => {
      element.onseeked = () => resolve();
    });
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

  const effectiveEnd = trimEnd > 0 ? trimEnd : (Number.isFinite(element.duration) ? element.duration : 0);
  const effectiveDuration = effectiveEnd > trimStart ? effectiveEnd - trimStart : 0;

  recorder.start(500);
  onProgress?.({
    progress: 0.3,
    message: 'Recording converted stream',
    detail: `Capturing ${mediaType} output with MediaRecorder.`,
    source: 'native',
  });
  const progressInterval = window.setInterval(() => {
    if (effectiveDuration > 0) {
      const elapsed = element.currentTime - trimStart;
      const completion = Math.min(1, elapsed / effectiveDuration);
      onProgress?.({
        progress: 0.3 + completion * 0.65,
        message: 'Recording converted stream',
        detail: `Captured ${Math.round(completion * 100)}% of the selected range.`,
        source: 'native',
      });
    }

    // Stop playback at trim end point
    if (trimEnd > 0 && element.currentTime >= trimEnd) {
      element.pause();
      element.dispatchEvent(new Event('ended'));
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

export async function convertVideoWithWebCodecs(args: {
  file: File;
  onProgress?: (activity: ConversionActivity) => void;
  signal?: AbortSignal;
}): Promise<ConversionResult> {
  const { file, onProgress, signal } = args;

  if (typeof VideoEncoder === 'undefined') {
    throw new Error('WebCodecs VideoEncoder is not supported in this browser.');
  }

  throwIfConversionAborted(signal);
  onProgress?.({
    progress: 0.05,
    message: 'Preparing WebCodecs AV1 encoder',
    detail: 'Loading muxer and setting up video element.',
    source: 'encoder',
  });

  const { Muxer, ArrayBufferTarget } = await import('webm-muxer');

  // Create video element to read source frames
  const video = document.createElement('video');
  video.preload = 'auto';
  video.muted = true;
  video.playsInline = true;
  video.src = URL.createObjectURL(file);

  await new Promise<void>((resolve, reject) => {
    video.onloadedmetadata = () => resolve();
    video.onerror = () => reject(new Error('Failed to load video metadata.'));
  });

  const width = video.videoWidth;
  const height = video.videoHeight;
  const duration = video.duration;

  if (!width || !height || !Number.isFinite(duration)) {
    URL.revokeObjectURL(video.src);
    throw new Error('Could not read video dimensions or duration.');
  }

  // Use 30fps for output, capped at source duration
  const fps = 30;
  const totalFrames = Math.ceil(duration * fps);

  const target = new ArrayBufferTarget();
  const muxer = new Muxer({
    target,
    video: { codec: 'V_AV1', width, height },
    type: 'webm',
  });

  let framesEncoded = 0;
  const encoder = new VideoEncoder({
    output: (chunk, meta) => {
      muxer.addVideoChunk(chunk, meta);
    },
    error: (e) => {
      throw new Error(`VideoEncoder error: ${e.message}`);
    },
  });

  const codecString = 'av01.0.04M.08';
  const support = await VideoEncoder.isConfigSupported({
    codec: codecString,
    width,
    height,
    bitrate: Math.min(5_000_000, width * height * 4),
    framerate: fps,
  });

  if (!support.supported) {
    URL.revokeObjectURL(video.src);
    throw new Error('AV1 encoding is not supported for this video configuration.');
  }

  encoder.configure({
    codec: codecString,
    width,
    height,
    bitrate: Math.min(5_000_000, width * height * 4),
    framerate: fps,
  });

  onProgress?.({
    progress: 0.1,
    message: 'Encoding video with AV1',
    detail: `Processing ${totalFrames} frames at ${fps} fps.`,
    source: 'encoder',
  });

  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  const ctx = canvas.getContext('2d')!;

  try {
    for (let i = 0; i < totalFrames; i++) {
      throwIfConversionAborted(signal);

      const time = i / fps;
      video.currentTime = time;
      await new Promise<void>((resolve) => {
        video.onseeked = () => resolve();
      });

      ctx.drawImage(video, 0, 0, width, height);
      const frame = new VideoFrame(canvas, {
        timestamp: time * 1_000_000,
        duration: (1 / fps) * 1_000_000,
      });

      encoder.encode(frame, { keyFrame: i % (fps * 2) === 0 });
      frame.close();
      framesEncoded++;

      if (i % 5 === 0 || i === totalFrames - 1) {
        const completion = (i + 1) / totalFrames;
        onProgress?.({
          progress: 0.1 + completion * 0.8,
          message: 'Encoding video with AV1',
          detail: `Encoded frame ${i + 1}/${totalFrames} (${Math.round(completion * 100)}%).`,
          source: 'encoder',
        });
        await new Promise<void>((r) => setTimeout(r, 0));
      }
    }

    await encoder.flush();
    muxer.finalize();

    const webmBlob = new Blob([target.buffer], { type: 'video/webm' });

    onProgress?.({
      progress: 0.98,
      message: 'AV1 video ready',
      detail: `Output size: ${(webmBlob.size / 1024 / 1024).toFixed(1)} MB (${framesEncoded} frames).`,
      source: 'encoder',
    });

    return {
      blob: webmBlob,
      outputName: replaceExtension(file.name, 'webm'),
      route: 'webcodecs-av1',
    };
  } finally {
    if (encoder.state !== 'closed') {
      encoder.close();
    }
    URL.revokeObjectURL(video.src);
  }
}
