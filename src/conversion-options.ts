import type { MediaKind } from './capabilities';

export type ImageConversionOptions = {
  width: number | null;
  height: number | null;
  keepAspectRatio: boolean;
};

export type MediaConversionOptions = {
  trimStart: number;
  trimEnd: number;
};

export type ConversionOptions = {
  outputBaseName: string;
  image: ImageConversionOptions;
  media: MediaConversionOptions;
};

export function stripExtension(name: string) {
  const extensionIndex = name.lastIndexOf('.');
  return extensionIndex > 0 ? name.slice(0, extensionIndex) : name;
}

export function extensionForMime(mime: string) {
  if (mime.includes('wav')) return 'wav';
  if (mime.includes('mpeg')) return 'mp3';
  if (mime.includes('mp4')) return 'mp4';
  if (mime.includes('webm')) return 'webm';
  if (mime.includes('ogg')) return 'ogg';
  if (mime.includes('png')) return 'png';
  if (mime.includes('jpeg')) return 'jpg';
  if (mime.includes('webp')) return 'webp';
  if (mime.includes('avif')) return 'avif';
  return 'bin';
}

export function buildOutputName(sourceName: string, targetMime: string, outputBaseName?: string) {
  const nextBaseName = outputBaseName?.trim() || stripExtension(sourceName);
  return `${nextBaseName}.${extensionForMime(targetMime)}`;
}

export function hasImageResize(options: ImageConversionOptions) {
  return Boolean(options.width || options.height);
}

export function hasMediaTrim(options: MediaConversionOptions) {
  return options.trimStart > 0 || options.trimEnd > 0;
}

export function describeSelectedOptions(mediaType: MediaKind, options: ConversionOptions) {
  const entries: string[] = [];

  if (mediaType === 'image' && hasImageResize(options.image)) {
    const resizeLabel = [
      options.image.width ? `${options.image.width}px wide` : null,
      options.image.height ? `${options.image.height}px tall` : null,
    ]
      .filter(Boolean)
      .join(' × ');
    entries.push(
      options.image.keepAspectRatio ? `Resize ${resizeLabel} with aspect ratio preserved` : `Resize ${resizeLabel}`,
    );
  }

  if ((mediaType === 'audio' || mediaType === 'video') && hasMediaTrim(options.media)) {
    const startLabel = `${options.media.trimStart.toFixed(1)}s`;
    const endLabel = options.media.trimEnd > 0 ? `${options.media.trimEnd.toFixed(1)}s` : 'the end';
    entries.push(`Trim from ${startLabel} to ${endLabel}`);
  }

  if (options.outputBaseName.trim()) {
    entries.push(`Save as ${options.outputBaseName.trim()}`);
  }

  return entries;
}
