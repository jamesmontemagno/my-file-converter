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
  const { id, type, file, targetMime, wasmModuleUrl } = (event.data || {}) as {
    id: number;
    type: string;
    file: File;
    targetMime: string;
    wasmModuleUrl: string;
  };

  if (type !== 'convert') return;

  try {
    if (!wasmModuleUrl) {
      throw new Error('WASM fallback is not configured.');
    }

    self.postMessage({ id, type: 'progress', progress: 0.1, message: 'Loading ffmpeg module' });
    const module = await import(/* @vite-ignore */ wasmModuleUrl) as {
      convert: (args: {
        file: File;
        targetMime: string;
        onProgress?: (progress: number, message: string) => void;
      }) => Promise<{ blob: Blob; outputName?: string }>;
    };

    if (typeof module.convert !== 'function') {
      throw new Error("WASM module must export a 'convert' function.");
    }

    const result = await module.convert({
      file,
      targetMime,
      onProgress: (progress, message) => {
        self.postMessage({ id, type: 'progress', progress, message });
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
