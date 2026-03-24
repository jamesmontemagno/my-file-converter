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
const DEFAULT_EXEC_TIMEOUT_MS = -1;

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

type OutputCodecConfig = {
  args: string[];
  summary?: string;
};

function codecConfigForTargetMime(targetMime: string): OutputCodecConfig {
  const normalized = targetMime.toLowerCase();

  if (normalized.startsWith('video/webm')) {
    if (normalized.includes('codecs=vp9')) {
      return {
        args: [
          '-c:v',
          'libvpx-vp9',
          '-deadline',
          'good',
          '-cpu-used',
          '4',
          '-b:v',
          '0',
          '-crf',
          '32',
          '-c:a',
          'libopus',
          '-b:a',
          '96k',
        ],
        summary: 'Encoding WebM with VP9 video and Opus audio. VP9 is the slower WebAssembly path for longer videos.',
      };
    }

    return {
      args: [
        '-c:v',
        'libvpx',
        '-deadline',
        'good',
        '-cpu-used',
        '4',
        '-b:v',
        '1M',
        '-crf',
        '10',
        '-c:a',
        'libopus',
        '-b:a',
        '96k',
      ],
      summary: 'Encoding WebM with VP8 video and Opus audio. VP8 is usually the faster WebAssembly path because it is less computationally expensive than VP9.',
    };
  }

  if (normalized.startsWith('audio/webm')) {
    return {
      args: ['-c:a', 'libopus', '-b:a', '96k'],
      summary: 'Encoding WebM audio with Opus.',
    };
  }

  if (normalized.startsWith('audio/ogg')) {
    return {
      args: ['-c:a', 'libopus', '-b:a', '96k'],
      summary: 'Encoding Ogg audio with Opus.',
    };
  }

  if (normalized.startsWith('video/mp4')) {
    return {
      args: ['-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '128k', '-movflags', '+faststart'],
      summary: 'Encoding MP4 with H.264 video and AAC audio.',
    };
  }

  if (normalized.startsWith('audio/mp4')) {
    return {
      args: ['-c:a', 'aac', '-b:a', '128k', '-movflags', '+faststart'],
      summary: 'Encoding MP4 audio with AAC.',
    };
  }

  return {
    args: [],
    summary: 'Using the container default codec settings for this target.',
  };
}

async function ensureLoaded(onProgress?: (activity: ConversionActivity) => void) {
  activeProgressHandler = onProgress;
  if (loaded) return;
  latestProgress = 0.08;
  onProgress?.({
    progress: latestProgress,
    message: 'Downloading ffmpeg core',
    detail: 'Fetching the ffmpeg.wasm runtime for the WebAssembly conversion path.',
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
  const codecConfig = codecConfigForTargetMime(targetMime);

  latestProgress = 0.2;
  onProgress?.({
    progress: latestProgress,
    message: 'Writing input file',
    detail: `Copying ${file.name} into the ffmpeg workspace.`,
    source: 'ffmpeg',
    rawOutput: `$ writeFile ${inputName}`,
  });
  await ffmpeg.writeFile(inputName, await fetchFile(file));

  try {
    // Place -ss before -i for input-level seeking (skips decoding of
    // skipped portion, which is significantly faster in WebAssembly).
    const command: string[] = [];

    if (trimStart > 0 || trimEnd > 0) {
      latestProgress = 0.35;
      onProgress?.({
        progress: latestProgress,
        message: 'Applying trim settings',
        detail: `Preparing trim window ${trimStart}s → ${trimEnd || 'end of file'}.`,
        source: 'ffmpeg',
      });
      if (trimStart > 0) command.push('-ss', trimStart.toString());
    }

    command.push('-i', inputName);

    // With input-level seeking (-ss before -i), -to is relative to the
    // seeked start point, so we output the correct duration.
    if (trimEnd > 0) {
      const duration = trimStart > 0 ? trimEnd - trimStart : trimEnd;
      if (duration > 0) command.push('-t', duration.toString());
    }

    if (codecConfig.summary) {
      latestProgress = Math.max(latestProgress, 0.38);
      onProgress?.({
        progress: latestProgress,
        message: 'Configuring output codec',
        detail: codecConfig.summary,
        source: 'ffmpeg',
      });
    }

    command.push(...codecConfig.args, outputName);
    latestProgress = Math.max(latestProgress, 0.4);
    onProgress?.({
      progress: latestProgress,
      message: 'Starting ffmpeg command',
      detail: codecConfig.summary
        ? `Running ffmpeg to create ${outputName}. ${codecConfig.summary}`
        : `Running ffmpeg to create ${outputName}.`,
      source: 'ffmpeg',
      rawOutput: `$ ffmpeg ${command.join(' ')}`,
    });
    onProgress?.({
      progress: latestProgress,
      message: 'ffmpeg command is running',
      detail:
        'The encoder is still working in the background. For longer single-thread jobs, log output can pause for a while before completion.',
      source: 'ffmpeg',
    });
    const exitCode = await ffmpeg.exec(command, DEFAULT_EXEC_TIMEOUT_MS);
    if (exitCode !== 0) {
      const outputExists = await ffmpeg
        .listDir('.')
        .then((nodes) => nodes.some((node) => node.name === outputName))
        .catch(() => false);
      throw new Error(
        outputExists
          ? `ffmpeg exited with code ${exitCode}. The output file exists but the command reported a non-zero status.`
          : `ffmpeg exited with code ${exitCode}. The command did not complete successfully before output collection.`,
      );
    }

    latestProgress = 0.96;
    onProgress?.({
      progress: latestProgress,
      message: 'ffmpeg command finished',
      detail: 'The encoding process returned successfully and output collection can begin.',
      source: 'ffmpeg',
    });
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
  } finally {
    await ffmpeg.deleteFile(inputName).catch(() => {});
    await ffmpeg.deleteFile(outputName).catch(() => {});
  }
}
