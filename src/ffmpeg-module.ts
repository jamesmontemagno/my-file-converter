import { FFmpeg } from '@ffmpeg/ffmpeg';
import { fetchFile, toBlobURL } from '@ffmpeg/util';

const ffmpeg = new FFmpeg();
let loaded = false;
let progressHandlerAttached = false;

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
  if (loaded) return;
  onProgress?.(0.1, 'Downloading ffmpeg core');
  const baseURL = 'https://cdn.jsdelivr.net/npm/@ffmpeg/core@0.12.10/dist/esm';
  await ffmpeg.load({
    coreURL: await toBlobURL(`${baseURL}/ffmpeg-core.js`, 'text/javascript'),
    wasmURL: await toBlobURL(`${baseURL}/ffmpeg-core.wasm`, 'application/wasm'),
  });
  if (!progressHandlerAttached) {
    ffmpeg.on('progress', ({ progress }) => {
      onProgress?.(Math.min(0.95, 0.15 + progress * 0.75), 'Transcoding with ffmpeg');
    });
    progressHandlerAttached = true;
  }
  loaded = true;
}

export async function convert(args: {
  file: File;
  targetMime: string;
  onProgress?: (progress: number, message: string) => void;
}) {
  const { file, targetMime, onProgress } = args;
  await ensureLoaded(onProgress);

  const inputExt = file.name.split('.').pop() || 'input';
  const outputExt = extForMime(targetMime);
  const inputName = `input.${inputExt}`;
  const outputName = `output.${outputExt}`;

  onProgress?.(0.2, 'Writing input file');
  await ffmpeg.writeFile(inputName, await fetchFile(file));
  await ffmpeg.exec(['-i', inputName, outputName]);
  const data = await ffmpeg.readFile(outputName);
  onProgress?.(1, 'Done');

  return {
    blob: new Blob([data instanceof Uint8Array ? data.slice().buffer : data], { type: targetMime }),
    outputName: `${file.name.replace(/\.[^/.]+$/, '')}.${outputExt}`,
  };
}
