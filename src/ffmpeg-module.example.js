// Example worker-compatible module contract for src/conversion-worker.js.
// Replace this with your real ffmpeg.wasm integration module and point
// the "ffmpeg worker module URL" field to its served URL.

export async function convert({ file, targetMime, onProgress }) {
  onProgress?.(0.1, "Loading converter");
  // TODO: integrate ffmpeg.wasm and perform real conversion.
  // This placeholder just returns the original file as a blob.
  onProgress?.(0.8, "Generating output");
  const blob = new Blob([await file.arrayBuffer()], { type: targetMime || file.type });
  const outputName = file.name;
  onProgress?.(1, "Done");
  return { blob, outputName };
}
