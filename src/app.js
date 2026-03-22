import { classifyMediaType, detectCapabilities, targetFormatsFor } from "./capabilities.js";
import { convertImage, convertViaMediaRecorder, supportsNativeRoute } from "./conversion.js";
import { convertWithWasmFallback } from "./worker-client.js";

const fileInput = document.getElementById("file-input");
const mediaTypeOut = document.getElementById("media-type");
const targetSelect = document.getElementById("target-format");
const qualityInput = document.getElementById("quality");
const qualityValue = document.getElementById("quality-value");
const convertBtn = document.getElementById("convert-btn");
const supportOutput = document.getElementById("support-output");
const statusEl = document.getElementById("status");
const progressEl = document.getElementById("progress");
const resultCard = document.getElementById("result-card");
const resultMeta = document.getElementById("result-meta");
const downloadLink = document.getElementById("download-link");
const enableWasmFallback = document.getElementById("enable-wasm-fallback");
const wasmModuleUrlInput = document.getElementById("wasm-module-url");

let selectedFile = null;
let lastBlobUrl = null;

function setStatus(message, progress = 0) {
  statusEl.textContent = message;
  progressEl.value = progress;
}

function resetResult() {
  resultCard.classList.add("hidden");
  resultMeta.textContent = "";
  if (lastBlobUrl) URL.revokeObjectURL(lastBlobUrl);
  lastBlobUrl = null;
}

function populateTargetOptions(mediaType) {
  const options = targetFormatsFor(mediaType);
  targetSelect.innerHTML = "";
  for (const option of options) {
    const el = document.createElement("option");
    el.value = option.value;
    el.textContent = option.label;
    targetSelect.appendChild(el);
  }
  targetSelect.disabled = options.length === 0;
}

function updateConvertEnabled() {
  convertBtn.disabled = !selectedFile || targetSelect.disabled;
}

function onFileChange() {
  resetResult();
  selectedFile = fileInput.files?.[0] || null;
  if (!selectedFile) {
    mediaTypeOut.textContent = "—";
    targetSelect.innerHTML = "";
    targetSelect.disabled = true;
    setStatus("Select a file to begin.", 0);
    updateConvertEnabled();
    return;
  }

  const mediaType = classifyMediaType(selectedFile);
  mediaTypeOut.textContent = `${mediaType} (${selectedFile.type || "unknown MIME"})`;
  populateTargetOptions(mediaType);
  setStatus("Ready to convert.", 0);
  updateConvertEnabled();
}

function showDownload(blob, outputName, route) {
  resetResult();
  lastBlobUrl = URL.createObjectURL(blob);
  downloadLink.href = lastBlobUrl;
  downloadLink.download = outputName;
  resultMeta.textContent = `Route: ${route} · Size: ${blob.size.toLocaleString()} bytes`;
  resultCard.classList.remove("hidden");
}

async function runConversion() {
  if (!selectedFile) return;
  const targetMime = targetSelect.value;
  const quality = Number(qualityInput.value);
  const mediaType = classifyMediaType(selectedFile);

  setStatus("Starting conversion...", 0.05);
  convertBtn.disabled = true;

  try {
    let result;

    if (supportsNativeRoute(selectedFile, targetMime)) {
      if (mediaType === "image") {
        result = await convertImage({
          file: selectedFile,
          targetMime,
          quality,
          onProgress: setStatusFromEngine,
        });
      } else {
        result = await convertViaMediaRecorder({
          file: selectedFile,
          targetMime,
          onProgress: setStatusFromEngine,
        });
      }
    } else if (enableWasmFallback.checked) {
      result = await convertWithWasmFallback({
        file: selectedFile,
        targetMime,
        wasmModuleUrl: wasmModuleUrlInput.value.trim(),
        onProgress: setStatusFromEngine,
      });
    } else {
      throw new Error(
        "Native route is not supported for this conversion. Enable WASM fallback and provide a module URL."
      );
    }

    showDownload(result.blob, result.outputName, result.route);
    setStatus("Conversion complete.", 1);
  } catch (error) {
    setStatus(`Conversion failed: ${error.message}`, 0);
  } finally {
    updateConvertEnabled();
  }
}

function setStatusFromEngine(progress, message) {
  setStatus(message || "Working...", Math.max(0, Math.min(1, progress ?? 0)));
}

const capabilities = detectCapabilities();
supportOutput.textContent = JSON.stringify(capabilities, null, 2);

fileInput.addEventListener("change", onFileChange);
convertBtn.addEventListener("click", runConversion);
qualityInput.addEventListener("input", () => {
  qualityValue.textContent = Number(qualityInput.value).toFixed(2);
});

onFileChange();
