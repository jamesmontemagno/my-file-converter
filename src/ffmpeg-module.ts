import { FFmpeg } from '@ffmpeg/ffmpeg';
import { fetchFile, toBlobURL } from '@ffmpeg/util';
import type { ConversionOptions } from './conversion-options';

const ffmpeg = new FFmpeg();
let loaded = false;
let progressHandlerAttached = false;
let activeProgressHandler: ((progress: number, message: string) => void) | undefined;

function extForMime(mime: string) {
  if (mime.includes('video/mp4')) return 'mp4';
  if (mime.includes('video/webm')) return 'webm';
  if (mime.includes('audio/ogg')) return 'ogg';
  if (mime.includes('audio/webm')) return 'webm';
  if (mime.includes('audio/mp4')) return 'm4a';
  if (mime.includes('image/png')) return 'png';
  if (mime.includes('image/jpeg')) return 'jpg';
  if (mime.includes('image/webp')) return 'webp';
  if (mime.includes('image/avif')) return 'avif';
  return 'bin';
}

async function ensureLoaded(onProgress?: (progress: number, message: string) => void) {
  activeProgressHandler = onProgress;
  if (loaded) return;
  onProgress?.(0.1, 'Downloading ffmpeg core');
  const baseURL = 'https://cdn.jsdelivr.net/npm/@ffmpeg/core@0.12.10/dist/esm';
  await ffmpeg.load({
    coreURL: await toBlobURL(`${baseURL}/ffmpeg-core.js`, 'text/javascript'),
    wasmURL: await toBlobURL(`${baseURL}/ffmpeg-core.wasm`, 'application/wasm'),
  });
  if (!progressHandlerAttached) {
    ffmpeg.on('progress', ({ progress }) => {
      activeProgressHandler?.(Math.min(0.95, 0.15 + progress * 0.75), 'Transcoding with ffmpeg');
    });
    progressHandlerAttached = true;
  }
  loaded = true;
}

export async function convert(args: {
  file: File;
  targetMime: string;
  options?: ConversionOptions;
  onProgress?: (progress: number, message: string) => void;
}) {
  const { file, targetMime, options, onProgress } = args;
  activeProgressHandler = onProgress;
  await ensureLoaded(onProgress);

  const inputExt = file.name.split('.').pop() || 'input';
  const outputExt = extForMime(targetMime);
  const inputName = `input.${inputExt}`;
  const outputName = `output.${outputExt}`;
  const trimStart = Math.max(0, options?.media.trimStart ?? 0);
  const trimEnd = Math.max(0, options?.media.trimEnd ?? 0);

  onProgress?.(0.2, 'Writing input file');
  await ffmpeg.writeFile(inputName, await fetchFile(file));
  const command = ['-i', inputName];

  if (trimStart > 0 || trimEnd > 0) {
    onProgress?.(0.35, 'Applying trim settings');
    if (trimStart > 0) command.push('-ss', trimStart.toString());
    if (trimEnd > 0) command.push('-to', trimEnd.toString());
  }

  command.push(outputName);
  await ffmpeg.exec(command);
  const data = await ffmpeg.readFile(outputName);
  onProgress?.(1, 'Done');

  return {
    blob: new Blob([data instanceof Uint8Array ? data.slice().buffer : data], { type: targetMime }),
    outputName: `${file.name.replace(/\.[^/.]+$/, '')}.${outputExt}`,
  };
}
