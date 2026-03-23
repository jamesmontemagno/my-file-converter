import { FFmpeg } from '@ffmpeg/ffmpeg';
import { fetchFile, toBlobURL } from '@ffmpeg/util';
import type { ConversionOptions } from './conversion-options';
import type { ConversionActivity } from './conversion';

const ffmpeg = new FFmpeg();
let loaded = false;
let progressHandlerAttached = false;
let logHandlerAttached = false;
let activeProgressHandler: ((activity: ConversionActivity) => void) | undefined;
let latestProgress = 0;

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

async function ensureLoaded(onProgress?: (activity: ConversionActivity) => void) {
  activeProgressHandler = onProgress;
  if (loaded) return;
  latestProgress = 0.08;
  onProgress?.({
    progress: latestProgress,
    message: 'Downloading ffmpeg core',
    detail: 'Fetching the ffmpeg.wasm runtime for the fallback route.',
    source: 'ffmpeg',
    rawOutput: '$ load ffmpeg-core',
  });
  const baseURL = 'https://cdn.jsdelivr.net/npm/@ffmpeg/core@0.12.10/dist/esm';
  await ffmpeg.load({
    coreURL: await toBlobURL(`${baseURL}/ffmpeg-core.js`, 'text/javascript'),
    wasmURL: await toBlobURL(`${baseURL}/ffmpeg-core.wasm`, 'application/wasm'),
  });
  if (!progressHandlerAttached) {
    ffmpeg.on('progress', ({ progress }) => {
      latestProgress = Math.min(0.95, 0.15 + progress * 0.75);
      activeProgressHandler?.({
        progress: latestProgress,
        message: 'Transcoding with ffmpeg',
        detail: `${Math.round(progress * 100)}% of the ffmpeg job has reported progress.`,
        source: 'ffmpeg',
      });
    });
    progressHandlerAttached = true;
  }
  if (!logHandlerAttached) {
    ffmpeg.on('log', ({ type, message }) => {
      const trimmed = message.trim();
      if (!trimmed) return;
      activeProgressHandler?.({
        progress: latestProgress,
        message: 'Transcoding with ffmpeg',
        detail: trimmed,
        rawOutput: `[${type}] ${trimmed}`,
        source: 'ffmpeg',
      });
    });
    logHandlerAttached = true;
  }
  loaded = true;
}

export async function convert(args: {
  file: File;
  targetMime: string;
  options?: ConversionOptions;
  onProgress?: (activity: ConversionActivity) => void;
}) {
  const { file, targetMime, options, onProgress } = args;
  activeProgressHandler = onProgress;
  latestProgress = 0;
  await ensureLoaded(onProgress);

  const inputExt = file.name.split('.').pop() || 'input';
  const outputExt = extForMime(targetMime);
  const inputName = `input.${inputExt}`;
  const outputName = `output.${outputExt}`;
  const trimStart = Math.max(0, options?.media.trimStart ?? 0);
  const trimEnd = Math.max(0, options?.media.trimEnd ?? 0);

  latestProgress = 0.2;
  onProgress?.({
    progress: latestProgress,
    message: 'Writing input file',
    detail: `Copying ${file.name} into the ffmpeg workspace.`,
    source: 'ffmpeg',
    rawOutput: `$ writeFile ${inputName}`,
  });
  await ffmpeg.writeFile(inputName, await fetchFile(file));
  const command = ['-i', inputName];

  if (trimStart > 0 || trimEnd > 0) {
    latestProgress = 0.35;
    onProgress?.({
      progress: latestProgress,
      message: 'Applying trim settings',
      detail: `Preparing trim window ${trimStart}s → ${trimEnd || 'end of file'}.`,
      source: 'ffmpeg',
    });
    if (trimStart > 0) command.push('-ss', trimStart.toString());
    if (trimEnd > 0) command.push('-to', trimEnd.toString());
  }

  command.push(outputName);
  latestProgress = Math.max(latestProgress, 0.4);
  onProgress?.({
    progress: latestProgress,
    message: 'Starting ffmpeg command',
    detail: `Running ffmpeg to create ${outputName}.`,
    source: 'ffmpeg',
    rawOutput: `$ ffmpeg ${command.join(' ')}`,
  });
  await ffmpeg.exec(command);
  latestProgress = 0.97;
  onProgress?.({
    progress: latestProgress,
    message: 'Collecting converted file',
    detail: `Reading ${outputName} back from ffmpeg.`,
    source: 'ffmpeg',
    rawOutput: `$ readFile ${outputName}`,
  });
  const data = await ffmpeg.readFile(outputName);
  onProgress?.({
    progress: 1,
    message: 'Done',
    detail: 'ffmpeg finished and returned the converted output.',
    source: 'ffmpeg',
  });

  return {
    blob: new Blob([data instanceof Uint8Array ? data.slice().buffer : data], { type: targetMime }),
    outputName: `${file.name.replace(/\.[^/.]+$/, '')}.${outputExt}`,
  };
}
