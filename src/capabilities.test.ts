import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  classifyMediaType,
  isTargetMimeSupported,
  targetFormatsFor,
  type CapabilityReport,
} from './capabilities';
import { supportsNativeRoute } from './conversion';

function makeCapabilities(overrides: Partial<CapabilityReport> = {}): CapabilityReport {
  return {
    mediaRecorder: true,
    mp3Encoder: true,
    webCodecs: {
      videoEncoder: true,
      audioEncoder: true,
      imageDecoder: true,
    },
    fileSystemAccess: {
      openPicker: true,
      savePicker: true,
    },
    workers: true,
    imageFormats: {
      'image/png': true,
      'image/jpeg': true,
      'image/webp': true,
      'image/avif': false,
    },
    mediaRecorderTypes: {
      'audio/webm;codecs=opus': true,
      'audio/ogg;codecs=opus': true,
      'audio/mp4': false,
      'video/webm;codecs=vp8,opus': true,
      'video/webm;codecs=vp9,opus': false,
      'video/mp4;codecs=avc1.42E01E,mp4a.40.2': true,
    },
    ...overrides,
  };
}

describe('capabilities', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  describe('classifyMediaType', () => {
    it('classifies images, audio, and video by MIME prefix', () => {
      expect(classifyMediaType(new File(['x'], 'photo.png', { type: 'image/png' }))).toBe('image');
      expect(classifyMediaType(new File(['x'], 'song.mp3', { type: 'audio/mpeg' }))).toBe('audio');
      expect(classifyMediaType(new File(['x'], 'movie.mp4', { type: 'video/mp4' }))).toBe('video');
    });

    it('returns unknown for nullish files or unsupported MIME prefixes', () => {
      expect(classifyMediaType(undefined)).toBe('unknown');
      expect(classifyMediaType(null)).toBe('unknown');
      expect(classifyMediaType(new File(['x'], 'data.bin', { type: 'application/octet-stream' }))).toBe('unknown');
      expect(classifyMediaType(new File(['x'], 'no-type'))).toBe('unknown');
    });
  });

  describe('targetFormatsFor', () => {
    it('returns expected image targets', () => {
      const targets = targetFormatsFor('image').map((entry) => entry.value);
      expect(targets).toEqual(['image/png', 'image/jpeg', 'image/webp', 'image/avif', 'image/gif', 'image/bmp']);
    });

    it('returns expected audio targets', () => {
      const targets = targetFormatsFor('audio').map((entry) => entry.value);
      expect(targets).toEqual([
        'audio/webm;codecs=opus',
        'audio/ogg;codecs=opus',
        'audio/mp4',
        'audio/mpeg',
        'audio/wav',
      ]);
    });

    it('returns expected video targets including extract-to-audio options', () => {
      const targets = targetFormatsFor('video').map((entry) => entry.value);
      expect(targets).toEqual([
        'video/webm;codecs=vp8,opus',
        'video/webm;codecs=vp9,opus',
        'video/mp4;codecs=avc1.42E01E,mp4a.40.2',
        'video/webm;codecs=av01',
        'audio/mpeg',
        'audio/wav',
      ]);
    });

    it('returns an empty list for unknown type', () => {
      expect(targetFormatsFor('unknown')).toEqual([]);
    });
  });

  describe('isTargetMimeSupported', () => {
    it('returns false when capability report is missing', () => {
      expect(isTargetMimeSupported('image/png', null)).toBe(false);
    });

    it('uses mp3Encoder flag for mp3 and wav software targets', () => {
      expect(isTargetMimeSupported('audio/mpeg', makeCapabilities({ mp3Encoder: true }))).toBe(true);
      expect(isTargetMimeSupported('audio/wav', makeCapabilities({ mp3Encoder: false }))).toBe(false);
    });

    it('uses image format map for image targets', () => {
      const caps = makeCapabilities();
      expect(isTargetMimeSupported('image/png', caps)).toBe(true);
      expect(isTargetMimeSupported('image/avif', caps)).toBe(false);
    });

    it('requires mediaRecorder plus a supported recorder mime for media targets', () => {
      const caps = makeCapabilities({ mediaRecorder: true });
      expect(isTargetMimeSupported('video/webm;codecs=vp8,opus', caps)).toBe(true);
      expect(isTargetMimeSupported('video/webm;codecs=vp9,opus', caps)).toBe(false);

      const noRecorder = makeCapabilities({ mediaRecorder: false });
      expect(isTargetMimeSupported('video/mp4;codecs=avc1.42E01E,mp4a.40.2', noRecorder)).toBe(false);
    });

    it('returns false for unknown non-media mime values', () => {
      expect(isTargetMimeSupported('application/json', makeCapabilities())).toBe(false);
    });
  });

  describe('supportsNativeRoute', () => {
    it('always supports image targets through native route selection', () => {
      const imageFile = new File(['x'], 'image.heic', { type: 'image/heic' });
      expect(supportsNativeRoute(imageFile, 'image/png')).toBe(true);
    });

    it('requires MediaRecorder support for audio/video native routes', () => {
      const audioFile = new File(['x'], 'voice.m4a', { type: 'audio/mp4' });

      vi.stubGlobal('MediaRecorder', {
        isTypeSupported: vi.fn((type: string) => type === 'audio/webm;codecs=opus'),
      });

      expect(supportsNativeRoute(audioFile, 'audio/webm;codecs=opus')).toBe(true);
      expect(supportsNativeRoute(audioFile, 'audio/ogg;codecs=opus')).toBe(false);
    });

    it('returns false for unknown file types', () => {
      const otherFile = new File(['x'], 'payload.bin', { type: 'application/octet-stream' });
      expect(supportsNativeRoute(otherFile, 'video/webm;codecs=vp8,opus')).toBe(false);
    });

    it('returns false when MediaRecorder is unavailable for media inputs', () => {
      vi.stubGlobal('MediaRecorder', undefined);
      const videoFile = new File(['x'], 'clip.mov', { type: 'video/quicktime' });
      expect(supportsNativeRoute(videoFile, 'video/mp4;codecs=avc1.42E01E,mp4a.40.2')).toBe(false);
    });
  });
});
