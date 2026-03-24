import type { ConversionOptions } from './conversion-options';
import type { ConversionActivity } from './conversion';
import { convert as convertWithBundledFfmpeg } from './ffmpeg-module';

function extensionFor(mime: string) {
  if (mime.includes('mp4')) return 'mp4';
  if (mime.includes('webm')) return 'webm';
  if (mime.includes('ogg')) return 'ogg';
  if (mime.includes('png')) return 'png';
  if (mime.includes('jpeg')) return 'jpg';
  if (mime.includes('webp')) return 'webp';
  if (mime.includes('avif')) return 'avif';
  return 'bin';
}

self.onmessage = async (event: MessageEvent) => {
  const { id, type, file, targetMime, wasmModuleUrl, options } = (event.data || {}) as {
    id: number;
    type: string;
    file: File;
    targetMime: string;
    wasmModuleUrl?: string;
    options?: ConversionOptions;
  };

  if (type !== 'convert') return;

  try {
    self.postMessage({ id, type: 'progress', progress: 0.1, message: 'Loading ffmpeg module' });
    const convert =
      wasmModuleUrl
        ? (
            await import(/* @vite-ignore */ wasmModuleUrl)
          as {
            convert?: (args: {
              file: File;
              targetMime: string;
              options?: ConversionOptions;
              onProgress?: (activity: ConversionActivity) => void;
            }) => Promise<{ blob: Blob; outputName?: string }>;
          }).convert
        : convertWithBundledFfmpeg;

    if (typeof convert !== 'function') {
      throw new Error("WASM module must export a 'convert' function.");
    }

    const result = await convert({
      file,
      targetMime,
      options,
      onProgress: (activity) => {
        self.postMessage({ id, type: 'progress', activity });
      },
    });

    self.postMessage({
      id,
      type: 'done',
      blob: result.blob,
      outputName: result.outputName || `${file.name.replace(/\.[^/.]+$/, '')}.${extensionFor(targetMime)}`,
      route: 'wasm-ffmpeg',
    });
  } catch (error) {
    self.postMessage({
      id,
      type: 'error',
      error: error instanceof Error ? error.message : 'Unknown worker error.',
    });
  }
};
