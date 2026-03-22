import { classifyMediaType } from "./capabilities.js";

function mimeToExt(mime) {
  if (mime.includes("png")) return "png";
  if (mime.includes("jpeg")) return "jpg";
  if (mime.includes("webp")) return "webp";
  if (mime.includes("avif")) return "avif";
  if (mime.includes("video/mp4")) return "mp4";
  if (mime.includes("video/webm")) return "webm";
  if (mime.includes("audio/ogg")) return "ogg";
  if (mime.includes("audio/webm")) return "webm";
  if (mime.includes("audio/mp4")) return "m4a";
  return "bin";
}

function replaceExtension(name, ext) {
  const idx = name.lastIndexOf(".");
  const base = idx > 0 ? name.slice(0, idx) : name;
  return `${base}.${ext}`;
}

async function readImageBitmap(file) {
  const bitmap = await createImageBitmap(file);
  return bitmap;
}

export async function convertImage({ file, targetMime, quality, onProgress }) {
  onProgress?.(0.1, "Decoding image");
  const bitmap = await readImageBitmap(file);

  const canvas = document.createElement("canvas");
  canvas.width = bitmap.width;
  canvas.height = bitmap.height;
  const ctx = canvas.getContext("2d");
  ctx.drawImage(bitmap, 0, 0);
  bitmap.close();

  onProgress?.(0.6, "Encoding image");
  const blob = await new Promise((resolve, reject) => {
    canvas.toBlob(
      (result) => {
        if (!result) {
          reject(new Error(`Unable to encode as ${targetMime}.`));
          return;
        }
        resolve(result);
      },
      targetMime,
      quality
    );
  });

  const outputName = replaceExtension(file.name, mimeToExt(targetMime));
  onProgress?.(1, "Done");
  return { blob, outputName, route: "native-image" };
}

async function createMediaElement(file, mediaType) {
  const el = document.createElement(mediaType === "audio" ? "audio" : "video");
  el.preload = "auto";
  el.muted = true;
  el.playsInline = true;
  el.src = URL.createObjectURL(file);
  await el.play().catch(() => {});
  el.pause();
  el.currentTime = 0;
  return el;
}

function mediaRecorderSupported(type) {
  return typeof MediaRecorder !== "undefined" && MediaRecorder.isTypeSupported(type);
}

export async function convertViaMediaRecorder({ file, targetMime, onProgress }) {
  const mediaType = classifyMediaType(file);
  if (mediaType !== "audio" && mediaType !== "video") {
    throw new Error("MediaRecorder conversion supports only audio/video input.");
  }

  if (!mediaRecorderSupported(targetMime)) {
    throw new Error(`MediaRecorder does not support target MIME type: ${targetMime}`);
  }

  onProgress?.(0.1, "Preparing media stream");
  const element = await createMediaElement(file, mediaType);

  if (!element.captureStream) {
    URL.revokeObjectURL(element.src);
    throw new Error("captureStream is not supported in this browser.");
  }

  const stream = element.captureStream();
  const chunks = [];
  const recorder = new MediaRecorder(stream, { mimeType: targetMime });

  const finished = new Promise((resolve, reject) => {
    recorder.ondataavailable = (event) => {
      if (event.data && event.data.size > 0) chunks.push(event.data);
    };
    recorder.onerror = (event) => {
      reject(event.error || new Error("MediaRecorder failed."));
    };
    recorder.onstop = () => resolve();
  });

  recorder.start(500);
  onProgress?.(0.3, "Recording converted stream");

  await new Promise((resolve, reject) => {
    element.onended = resolve;
    element.onerror = () => reject(new Error("Playback failed for source media."));
    element.play().catch(reject);
  });

  recorder.stop();
  await finished;
  stream.getTracks().forEach((t) => t.stop());
  URL.revokeObjectURL(element.src);

  const blob = new Blob(chunks, { type: targetMime });
  const outputName = replaceExtension(file.name, mimeToExt(targetMime));
  onProgress?.(1, "Done");

  return { blob, outputName, route: "native-mediarecorder" };
}

export function supportsNativeRoute(file, targetMime) {
  const mediaType = classifyMediaType(file);
  if (mediaType === "image") return true;
  if (mediaType === "audio" || mediaType === "video") return mediaRecorderSupported(targetMime);
  return false;
}
