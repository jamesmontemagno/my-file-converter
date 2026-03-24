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

// ---------------------------------------------------------------------------
// File-size safety: the single-thread WASM build has a ~2 GB memory ceiling.
// Input + working buffers + output must all fit, so reject files over 2 GB.
// In practice the usable ceiling is lower; 2 GB is a hard WebAssembly limit.
// ---------------------------------------------------------------------------
const MAX_INPUT_BYTES = 2 * 1024 * 1024 * 1024; // 2 GB

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
  summary: string;
};

// ---------------------------------------------------------------------------
// Encoding presets tuned for the single-thread WebAssembly build.
//
// Key constraints in browser WASM:
//   - Single-threaded: no pthreads, no row-mt, no multi-slice.
//   - ~10–20× slower than native FFmpeg per the upstream FAQ.
//   - Progress events rely on stderr frame lines; if the encoder is stuck on
//     a single expensive frame, no progress fires until that frame completes.
//
// Guidelines applied below:
//   VP8:  -deadline realtime -cpu-used 8          (fastest VP8 mode)
//   VP9:  -deadline realtime -cpu-used 8 -row-mt 0 (fastest VP9 mode)
//   x264: -preset ultrafast                       (fastest x264 mode)
//   Audio-only jobs keep simple settings — they are fast in any mode.
// ---------------------------------------------------------------------------

function codecConfigForTargetMime(targetMime: string): OutputCodecConfig {
  const normalized = targetMime.toLowerCase();

  // -- Video WebM (VP9 variant) -------------------------------------------
  if (normalized.startsWith('video/webm') && normalized.includes('codecs=vp9')) {
    return {
      args: [
        '-c:v', 'libvpx-vp9',
        '-deadline', 'realtime',
        '-cpu-used', '8',
        '-row-mt', '0',
        '-b:v', '1M',
        '-crf', '35',
        '-c:a', 'libopus',
        '-b:a', '96k',
      ],
      summary:
        'WebM VP9 + Opus — realtime deadline / cpu-used 8 for WASM speed. ' +
        'VP9 is still slower than VP8 in single-thread WebAssembly; expect long encodes for videos over a few minutes.',
    };
  }

  // -- Video WebM (VP8 default) -------------------------------------------
  if (normalized.startsWith('video/webm')) {
    return {
      args: [
        '-c:v', 'libvpx',
        '-deadline', 'realtime',
        '-cpu-used', '8',
        '-b:v', '1M',
        '-crf', '30',
        '-c:a', 'libopus',
        '-b:a', '96k',
      ],
      summary:
        'WebM VP8 + Opus — realtime deadline / cpu-used 8 for fast WASM encoding. ' +
        'VP8 is the fastest video codec available in this WebAssembly build.',
    };
  }

  // -- Audio WebM ---------------------------------------------------------
  if (normalized.startsWith('audio/webm')) {
    return {
      args: ['-vn', '-c:a', 'libopus', '-b:a', '96k'],
      summary: 'WebM audio with Opus.',
    };
  }

  // -- Audio Ogg ----------------------------------------------------------
  if (normalized.startsWith('audio/ogg')) {
    return {
      args: ['-vn', '-c:a', 'libopus', '-b:a', '96k'],
      summary: 'Ogg audio with Opus.',
    };
  }

  // -- Video MP4 ----------------------------------------------------------
  if (normalized.startsWith('video/mp4')) {
    return {
      args: [
        '-c:v', 'libx264',
        '-preset', 'ultrafast',
        '-pix_fmt', 'yuv420p',
        '-c:a', 'aac',
        '-b:a', '128k',
        '-movflags', '+faststart',
      ],
      summary:
        'MP4 H.264 + AAC — ultrafast preset for WASM speed. ' +
        'Quality is lower than the default medium preset but encoding completes orders of magnitude faster in WebAssembly.',
    };
  }

  // -- Audio MP4 ----------------------------------------------------------
  if (normalized.startsWith('audio/mp4')) {
    return {
      args: ['-vn', '-c:a', 'aac', '-b:a', '128k', '-movflags', '+faststart'],
      summary: 'MP4 audio with AAC.',
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
  latestProgress = 0.05;
  onProgress?.({
    progress: latestProgress,
    message: 'Downloading ffmpeg core',
    detail: 'Fetching the ffmpeg.wasm runtime (~31 MB) from the CDN. This is cached after the first load.',
    source: 'ffmpeg',
    rawOutput: '$ load ffmpeg-core',
  });
  const baseURL = 'https://cdn.jsdelivr.net/npm/@ffmpeg/core@0.12.10/dist/esm';
  await ffmpeg.load({
    coreURL: await toBlobURL(`${baseURL}/ffmpeg-core.js`, 'text/javascript'),
    wasmURL: await toBlobURL(`${baseURL}/ffmpeg-core.wasm`, 'application/wasm'),
  });
  if (!progressHandlerAttached) {
    ffmpeg.on('progress', ({ progress, time }) => {
      latestProgress = Math.min(0.95, 0.10 + progress * 0.80);
      const pct = Math.round(progress * 100);
      const timeSec = time > 0 ? (time / 1_000_000).toFixed(1) : null;
      activeProgressHandler?.({
        progress: latestProgress,
        message: 'Transcoding with ffmpeg',
        detail: timeSec
          ? `${pct}% complete — encoded ${timeSec}s of output so far.`
          : `${pct}% of the ffmpeg job has reported progress.`,
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

  // ---- Input validation -------------------------------------------------
  if (file.size >= MAX_INPUT_BYTES) {
    throw new Error(
      `Input file is ${(file.size / (1024 * 1024 * 1024)).toFixed(1)} GB. ` +
      'The WebAssembly runtime has a hard memory ceiling of ~2 GB, so files this large cannot be processed in the browser.',
    );
  }

  await ensureLoaded(onProgress);

  const inputExt = file.name.split('.').pop() || 'input';
  const outputExt = extForMime(targetMime);
  const inputName = `input.${inputExt}`;
  const outputName = `output.${outputExt}`;
  const trimStart = Math.max(0, options?.media.trimStart ?? 0);
  const trimEnd = Math.max(0, options?.media.trimEnd ?? 0);
  const codecConfig = codecConfigForTargetMime(targetMime);

  // ---- Write input into the WASM virtual filesystem ---------------------
  latestProgress = 0.08;
  onProgress?.({
    progress: latestProgress,
    message: 'Writing input file',
    detail: `Copying ${file.name} (${(file.size / (1024 * 1024)).toFixed(1)} MB) into the ffmpeg virtual filesystem.`,
    source: 'ffmpeg',
    rawOutput: `$ writeFile ${inputName}`,
  });
  await ffmpeg.writeFile(inputName, await fetchFile(file));

  try {
    // ---- Build command --------------------------------------------------
    // Place -ss before -i for input-level seeking (skips decoding of the
    // skipped portion, which is significantly faster in WebAssembly).
    const command: string[] = [];

    if (trimStart > 0 || trimEnd > 0) {
      onProgress?.({
        progress: 0.12,
        message: 'Applying trim settings',
        detail: `Trim window: ${trimStart}s → ${trimEnd || 'end of file'}.`,
        source: 'ffmpeg',
      });
      if (trimStart > 0) command.push('-ss', trimStart.toString());
    }

    command.push('-i', inputName);

    // With input-level seeking (-ss before -i), -to is relative to the
    // seeked start point, so we compute the correct duration.
    if (trimEnd > 0) {
      const duration = trimStart > 0 ? trimEnd - trimStart : trimEnd;
      if (duration > 0) command.push('-t', duration.toString());
    }

    command.push(...codecConfig.args, outputName);

    // ---- Report what we are about to run --------------------------------
    latestProgress = 0.15;
    onProgress?.({
      progress: latestProgress,
      message: 'Starting ffmpeg encode',
      detail: codecConfig.summary,
      source: 'ffmpeg',
      rawOutput: `$ ffmpeg ${command.join(' ')}`,
    });

    // ---- Execute --------------------------------------------------------
    // exec() returns 0 on success, 1 on timeout, other non-zero on error.
    // Default timeout is -1 (no timeout).
    // The WASM module can also throw non-Error values (e.g. Emscripten
    // abort strings) so we catch broadly and wrap for clarity.
    let exitCode: number;
    try {
      exitCode = await ffmpeg.exec(command);
    } catch (execError) {
      const detail =
        execError instanceof Error
          ? execError.message
          : typeof execError === 'string'
            ? execError
            : String(execError);
      throw new Error(
        `ffmpeg crashed during encoding: ${detail}. ` +
        'This can happen when the WebAssembly runtime runs out of memory. ' +
        'Try a shorter clip, a smaller file, or a different output format.',
      );
    }

    if (exitCode !== 0) {
      throw new Error(
        exitCode === 1
          ? 'ffmpeg timed out before the conversion could finish.'
          : `ffmpeg exited with code ${exitCode}. Check the raw log output for encoder error details.`,
      );
    }

    // ---- Read output ----------------------------------------------------
    latestProgress = 0.97;
    onProgress?.({
      progress: latestProgress,
      message: 'Reading output file',
      detail: `Collecting ${outputName} from the ffmpeg virtual filesystem.`,
      source: 'ffmpeg',
      rawOutput: `$ readFile ${outputName}`,
    });

    let data: Uint8Array | string;
    try {
      data = await ffmpeg.readFile(outputName);
    } catch {
      throw new Error(
        'ffmpeg reported success but no output file was created. ' +
        'This usually means the input format or codec combination is not supported by this WebAssembly build.',
      );
    }

    if (data instanceof Uint8Array && data.byteLength === 0) {
      throw new Error(
        'ffmpeg created an empty output file. ' +
        'The input may be too short, or the selected codec could not produce output for this source.',
      );
    }

    onProgress?.({
      progress: 1,
      message: 'Conversion complete',
      detail: 'ffmpeg finished successfully.',
      source: 'ffmpeg',
    });

    return {
      blob: new Blob([data instanceof Uint8Array ? data.slice().buffer : data], { type: targetMime }),
      outputName: `${file.name.replace(/\.[^/.]+$/, '')}.${outputExt}`,
    };
  } finally {
    // Clean up virtual filesystem regardless of success or failure.
    await ffmpeg.deleteFile(inputName).catch(() => {});
    await ffmpeg.deleteFile(outputName).catch(() => {});
  }
}
