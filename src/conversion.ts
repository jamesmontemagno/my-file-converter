import { classifyMediaType } from './capabilities';

export type ConversionResult = {
  blob: Blob;
  outputName: string;
  route: string;
};

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
  onProgress?: (progress: number, message: string) => void;
}): Promise<ConversionResult> {
  const { file, targetMime, quality, onProgress } = args;
  onProgress?.(0.1, 'Decoding image');
  const bitmap = await createImageBitmap(file);
  const canvas = document.createElement('canvas');
  canvas.width = bitmap.width;
  canvas.height = bitmap.height;
  const ctx = canvas.getContext('2d');
  if (!ctx) {
    bitmap.close();
    throw new Error('Could not create 2D canvas context.');
  }
  ctx.drawImage(bitmap, 0, 0);
  bitmap.close();

  onProgress?.(0.6, 'Encoding image');
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
  onProgress?: (progress: number, message: string) => void;
}): Promise<ConversionResult> {
  const { file, targetMime, onProgress } = args;
  const mediaType = classifyMediaType(file);
  if (mediaType !== 'audio' && mediaType !== 'video') {
    throw new Error('MediaRecorder conversion supports only audio/video input.');
  }
  if (!mediaRecorderSupported(targetMime)) {
    throw new Error(`MediaRecorder does not support target MIME type: ${targetMime}`);
  }

  onProgress?.(0.1, 'Preparing media stream');
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
  onProgress?.(0.3, 'Recording converted stream');

  await new Promise<void>((resolve, reject) => {
    element.onended = () => resolve();
    element.onerror = () => reject(new Error('Playback failed for source media.'));
    element.play().catch(reject);
  });

  recorder.stop();
  await finished;
  stream.getTracks().forEach((track) => track.stop());
  URL.revokeObjectURL(element.src);

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
