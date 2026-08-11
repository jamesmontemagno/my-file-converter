import type { MediaKind } from './capabilities';

export type ImageConversionOptions = {
  width: number | null;
  height: number | null;
  keepAspectRatio: boolean;
};

import type { AudioChannelMode } from './conversion';

export type MediaConversionOptions = {
  trimStart: number;
  trimEnd: number;
  channelMode: AudioChannelMode;
  videoEncodingSpeed: 'fast' | 'balanced' | 'quality';
  videoFrameRate: number | null;
  audioBitrate: number | null;
  audioSampleRate: number | null;
  wavBitDepth: 16 | 24 | 32;
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
  if (mime.includes('gif')) return 'gif';
  if (mime.includes('bmp')) return 'bmp';
  return 'bin';
}

export function buildOutputName(sourceName: string, targetMime: string, outputBaseName?: string) {
  const nextBaseName = outputBaseName?.trim() || stripExtension(sourceName);
  return `${nextBaseName}.${extensionForMime(targetMime)}`;
}

export function hasImageResize(options: ImageConversionOptions) {
  return Boolean(options.width || options.height);
}

export function hasMediaTrim(options: Pick<MediaConversionOptions, 'trimStart' | 'trimEnd'>) {
  return options.trimStart > 0 || options.trimEnd > 0;
}

export function describeSelectedOptions(mediaType: MediaKind, options: ConversionOptions, targetMime?: string) {
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

  if ((mediaType === 'audio' || mediaType === 'video') && options.media.channelMode !== 'auto') {
    entries.push(`Output channels: ${options.media.channelMode}`);
  }

  if (targetMime?.startsWith('video/')) {
    if (options.media.videoEncodingSpeed !== 'balanced') {
      entries.push(`Video encoding speed: ${options.media.videoEncodingSpeed}`);
    }
    if (options.media.videoFrameRate) {
      entries.push(`Video frame rate: ${options.media.videoFrameRate} fps`);
    }
  }

  if (targetMime === 'audio/mpeg' || targetMime?.startsWith('video/')) {
    if (options.media.audioBitrate) {
      entries.push(`Audio bitrate: ${options.media.audioBitrate} kbps`);
    }
    if (options.media.audioSampleRate) {
      entries.push(`Audio sample rate: ${options.media.audioSampleRate} Hz`);
    }
  }

  if (targetMime === 'audio/wav') {
    if (options.media.audioSampleRate) {
      entries.push(`Audio sample rate: ${options.media.audioSampleRate} Hz`);
    }
    if (options.media.wavBitDepth !== 16) {
      entries.push(`WAV bit depth: ${options.media.wavBitDepth}-bit`);
    }
  }

  if (options.outputBaseName.trim()) {
    entries.push(`Save as ${options.outputBaseName.trim()}`);
  }

  return entries;
}
