import {
  createConversionAbortError,
  type ConversionActivity,
  type ConversionResult,
} from './conversion';
import type { ConversionOptions } from './conversion-options';

type PendingEntry = {
  resolve: (value: ConversionResult) => void;
  reject: (reason?: unknown) => void;
  onProgress?: (activity: ConversionActivity) => void;
  cleanup?: () => void;
};

let worker: Worker | undefined;
let nextId = 1;
const pending = new Map<number, PendingEntry>();

function settlePending(id: number, reason: 'done' | 'error', payload: ConversionResult | Error) {
  const entry = pending.get(id);
  if (!entry) return;
  pending.delete(id);
  entry.cleanup?.();

  if (reason === 'done') {
    entry.resolve(payload as ConversionResult);
    return;
  }

  entry.reject(payload);
}

function resetWorker(reason: unknown) {
  const activeWorker = worker;
  worker = undefined;
  if (activeWorker) {
    activeWorker.onmessage = null;
    activeWorker.terminate();
  }

  for (const [id, entry] of pending.entries()) {
    pending.delete(id);
    entry.cleanup?.();
    entry.reject(reason);
  }
}

function getWorker() {
  if (!worker) {
    worker = new Worker(new URL('./conversion-worker.ts', import.meta.url), { type: 'module' });
    worker.onmessage = (event: MessageEvent) => {
      const { id, type } = event.data as {
        id: number;
        type: string;
        activity?: ConversionActivity;
        error?: string;
      };
      const entry = pending.get(id);
      if (!entry) return;

      if (type === 'progress') {
        if (event.data.activity) {
          entry.onProgress?.(event.data.activity);
        }
        return;
      }
      if (type === 'done') {
        settlePending(id, 'done', event.data as ConversionResult);
        return;
      }
      if (type === 'error') {
        settlePending(id, 'error', new Error(event.data.error || 'Worker conversion failed.'));
      }
    };
    worker.onerror = () => {
      resetWorker(new Error('Worker conversion failed.'));
    };
  }
  return worker;
}

export function convertWithWasmFallback(args: {
  file: File;
  targetMime: string;
  wasmModuleUrl?: string;
  options?: ConversionOptions;
  onProgress?: (activity: ConversionActivity) => void;
  signal?: AbortSignal;
}) {
  if (args.signal?.aborted) {
    return Promise.reject(createConversionAbortError());
  }

  const id = nextId++;
  const w = getWorker();
  return new Promise<ConversionResult>((resolve, reject) => {
    const handleAbort = () => {
      resetWorker(createConversionAbortError());
    };
    const cleanup = () => args.signal?.removeEventListener('abort', handleAbort);

    pending.set(id, { resolve, reject, onProgress: args.onProgress, cleanup });
    args.signal?.addEventListener('abort', handleAbort, { once: true });
    w.postMessage({
      id,
      type: 'convert',
      file: args.file,
      targetMime: args.targetMime,
      wasmModuleUrl: args.wasmModuleUrl,
      options: args.options,
    });
  });
}
