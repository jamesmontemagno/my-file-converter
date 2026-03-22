let worker;
let nextId = 1;
const pending = new Map();

function getWorker() {
  if (!worker) {
    worker = new Worker("./src/conversion-worker.js", { type: "module" });
    worker.onmessage = (event) => {
      const { id, type } = event.data;
      const entry = pending.get(id);
      if (!entry) return;

      if (type === "progress") {
        entry.onProgress?.(event.data.progress, event.data.message);
        return;
      }

      if (type === "done") {
        pending.delete(id);
        entry.resolve(event.data);
        return;
      }

      if (type === "error") {
        pending.delete(id);
        entry.reject(new Error(event.data.error || "Worker conversion failed."));
      }
    };
  }
  return worker;
}

export function convertWithWasmFallback({ file, targetMime, wasmModuleUrl, onProgress }) {
  const id = nextId++;
  const w = getWorker();

  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject, onProgress });
    w.postMessage({ id, type: "convert", file, targetMime, wasmModuleUrl });
  });
}
