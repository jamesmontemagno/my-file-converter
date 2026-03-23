import { classifyMediaType } from './capabilities';
import type { ImageConversionOptions } from './conversion-options';

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
  source?: 'native' | 'ffmpeg';
};

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

export async function convertImage(args: {
  file: File;
  targetMime: string;
  quality: number;
  imageOptions?: ImageConversionOptions;
  onProgress?: (activity: ConversionActivity) => void;
}): Promise<ConversionResult> {
  const { file, targetMime, quality, imageOptions, onProgress } = args;
  onProgress?.({
    progress: 0.1,
    message: 'Decoding image',
    detail: `Reading ${file.type || 'source image'} into the browser canvas.`,
    source: 'native',
  });
  const bitmap = await createImageBitmap(file);
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

  onProgress?.({
    progress: 0.6,
    message: 'Encoding image',
    detail: `Exporting the canvas as ${targetMime}.`,
    source: 'native',
  });
  const blob = await new Promise<Blob>((resolve, reject) => {
    canvas.toBlob(
      (result) => {
        if (!result) {
          reject(new Error(`Unable to encode as ${targetMime}.`));
          return;
        }
        resolve(result);
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

export async function convertViaMediaRecorder(args: {
  file: File;
  targetMime: string;
  onProgress?: (activity: ConversionActivity) => void;
}): Promise<ConversionResult> {
  const { file, targetMime, onProgress } = args;
  const mediaType = classifyMediaType(file);
  if (mediaType !== 'audio' && mediaType !== 'video') {
    throw new Error('MediaRecorder conversion supports only audio/video input.');
  }
  if (!mediaRecorderSupported(targetMime)) {
    throw new Error(`MediaRecorder does not support target MIME type: ${targetMime}`);
  }

  onProgress?.({
    progress: 0.1,
    message: 'Preparing media stream',
    detail: 'Loading the source media into a browser playback element.',
    source: 'native',
  });
  const element = await createMediaElement(file, mediaType);
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
    await new Promise<void>((resolve, reject) => {
      element.onended = () => resolve();
      element.onerror = () => reject(new Error('Playback failed for source media.'));
      element.play().catch(reject);
    });

    onProgress?.({
      progress: 0.96,
      message: 'Finalizing recording',
      detail: 'Stopping the recorder and assembling the output file.',
      source: 'native',
    });
    recorder.stop();
    await finished;
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
