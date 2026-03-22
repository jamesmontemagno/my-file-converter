import type { ConversionResult } from './conversion';

type PendingEntry = {
  resolve: (value: ConversionResult) => void;
  reject: (reason?: unknown) => void;
  onProgress?: (progress: number, message: string) => void;
};

let worker: Worker | undefined;
let nextId = 1;
const pending = new Map<number, PendingEntry>();

function getWorker() {
  if (!worker) {
    worker = new Worker(new URL('./conversion-worker.ts', import.meta.url), { type: 'module' });
    worker.onmessage = (event: MessageEvent) => {
      const { id, type } = event.data as { id: number; type: string; progress?: number; message?: string; error?: string };
      const entry = pending.get(id);
      if (!entry) return;

      if (type === 'progress') {
        entry.onProgress?.(event.data.progress, event.data.message);
        return;
      }
      if (type === 'done') {
        pending.delete(id);
        entry.resolve(event.data as ConversionResult);
        return;
      }
      if (type === 'error') {
        pending.delete(id);
        entry.reject(new Error(event.data.error || 'Worker conversion failed.'));
      }
    };
  }
  return worker;
}

export function convertWithWasmFallback(args: {
  file: File;
  targetMime: string;
  wasmModuleUrl: string;
  onProgress?: (progress: number, message: string) => void;
}) {
  const id = nextId++;
  const w = getWorker();
  return new Promise<ConversionResult>((resolve, reject) => {
    pending.set(id, { resolve, reject, onProgress: args.onProgress });
    w.postMessage({ id, type: 'convert', file: args.file, targetMime: args.targetMime, wasmModuleUrl: args.wasmModuleUrl });
  });
}
