function extensionFor(mime) {
  if (mime.includes("mp4")) return "mp4";
  if (mime.includes("webm")) return "webm";
  if (mime.includes("ogg")) return "ogg";
  if (mime.includes("png")) return "png";
  if (mime.includes("jpeg")) return "jpg";
  if (mime.includes("webp")) return "webp";
  if (mime.includes("avif")) return "avif";
  return "bin";
}

self.onmessage = async (event) => {
  const { id, type, file, targetMime, wasmModuleUrl } = event.data || {};
  if (type !== "convert") return;

  try {
    if (!wasmModuleUrl) {
      throw new Error(
        "WASM fallback is not configured. Provide a worker-compatible ffmpeg module URL in Advanced options."
      );
    }

    self.postMessage({ id, type: "progress", progress: 0.1, message: "Loading wasm module" });

    // Expected module contract (custom integration point):
    // export async function convert({ file, targetMime, onProgress }) => { blob, outputName }
    const module = await import(wasmModuleUrl);
    if (typeof module.convert !== "function") {
      throw new Error("WASM module must export a 'convert' function.");
    }

    const result = await module.convert({
      file,
      targetMime,
      onProgress: (progress, message) => {
        self.postMessage({ id, type: "progress", progress, message });
      },
    });

    if (!result?.blob) {
      throw new Error("WASM module returned no output blob.");
    }

    const outputName =
      result.outputName ||
      `${(file?.name || "output").replace(/\.[^/.]+$/, "")}.${extensionFor(targetMime)}`;

    self.postMessage({
      id,
      type: "done",
      blob: result.blob,
      outputName,
      route: "wasm-fallback",
    });
  } catch (error) {
    self.postMessage({
      id,
      type: "error",
      error: error?.message || "Unknown worker error.",
    });
  }
};
