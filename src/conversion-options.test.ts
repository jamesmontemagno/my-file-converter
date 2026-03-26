import { describe, expect, it } from 'vitest';

import {
  buildOutputName,
  describeSelectedOptions,
  extensionForMime,
  hasImageResize,
  hasMediaTrim,
  stripExtension,
  type ConversionOptions,
} from './conversion-options';

describe('conversion-options', () => {
  describe('stripExtension', () => {
    it('removes the final extension from a standard filename', () => {
      expect(stripExtension('photo.png')).toBe('photo');
    });

    it('keeps names without extensions untouched', () => {
      expect(stripExtension('README')).toBe('README');
    });

    it('keeps dotfiles untouched', () => {
      expect(stripExtension('.env')).toBe('.env');
    });

    it('only removes the last extension segment', () => {
      expect(stripExtension('archive.tar.gz')).toBe('archive.tar');
    });
  });

  describe('extensionForMime', () => {
    it('maps known MIME families to expected extensions', () => {
      expect(extensionForMime('audio/wav')).toBe('wav');
      expect(extensionForMime('audio/mpeg')).toBe('mp3');
      expect(extensionForMime('audio/mp4')).toBe('mp4');
      expect(extensionForMime('audio/ogg;codecs=opus')).toBe('ogg');
      expect(extensionForMime('video/webm;codecs=vp9,opus')).toBe('webm');
      expect(extensionForMime('image/png')).toBe('png');
      expect(extensionForMime('image/jpeg')).toBe('jpg');
      expect(extensionForMime('image/webp')).toBe('webp');
      expect(extensionForMime('image/avif')).toBe('avif');
    });

    it('falls back to bin for unknown mime values', () => {
      expect(extensionForMime('application/octet-stream')).toBe('bin');
    });
  });

  describe('buildOutputName', () => {
    it('uses trimmed custom output base name when provided', () => {
      const name = buildOutputName('holiday.mov', 'video/webm;codecs=vp8,opus', '  trailer-final  ');

      expect(name).toBe('trailer-final.webm');
    });

    it('falls back to source basename when output name is missing', () => {
      const name = buildOutputName('voice-note.m4a', 'audio/mpeg');

      expect(name).toBe('voice-note.mp3');
    });

    it('falls back to source basename when output name is only whitespace', () => {
      const name = buildOutputName('poster.jpeg', 'image/png', '   ');

      expect(name).toBe('poster.png');
    });
  });

  describe('hasImageResize', () => {
    it('returns true if width is set', () => {
      expect(hasImageResize({ width: 800, height: null, keepAspectRatio: true })).toBe(true);
    });

    it('returns true if height is set', () => {
      expect(hasImageResize({ width: null, height: 600, keepAspectRatio: true })).toBe(true);
    });

    it('returns false if neither dimension is set', () => {
      expect(hasImageResize({ width: null, height: null, keepAspectRatio: true })).toBe(false);
    });
  });

  describe('hasMediaTrim', () => {
    it('returns true when trim start is set', () => {
      expect(hasMediaTrim({ trimStart: 2.5, trimEnd: 0 })).toBe(true);
    });

    it('returns true when trim end is set', () => {
      expect(hasMediaTrim({ trimStart: 0, trimEnd: 42 })).toBe(true);
    });

    it('returns false when neither trim boundary is set', () => {
      expect(hasMediaTrim({ trimStart: 0, trimEnd: 0 })).toBe(false);
    });
  });

  describe('describeSelectedOptions', () => {
    const baseOptions: ConversionOptions = {
      outputBaseName: '',
      image: {
        width: null,
        height: null,
        keepAspectRatio: true,
      },
      media: {
        trimStart: 0,
        trimEnd: 0,
      },
    };

    it('returns resize text for image options with aspect ratio preserved', () => {
      const entries = describeSelectedOptions('image', {
        ...baseOptions,
        image: {
          width: 1280,
          height: 720,
          keepAspectRatio: true,
        },
      });

      expect(entries).toEqual(['Resize 1280px wide × 720px tall with aspect ratio preserved']);
    });

    it('returns resize text without aspect-ratio note when disabled', () => {
      const entries = describeSelectedOptions('image', {
        ...baseOptions,
        image: {
          width: 512,
          height: null,
          keepAspectRatio: false,
        },
      });

      expect(entries).toEqual(['Resize 512px wide']);
    });

    it('returns media trim text for audio and video', () => {
      const audioEntries = describeSelectedOptions('audio', {
        ...baseOptions,
        media: { trimStart: 1, trimEnd: 3.5 },
      });
      const videoEntries = describeSelectedOptions('video', {
        ...baseOptions,
        media: { trimStart: 0.5, trimEnd: 0 },
      });

      expect(audioEntries).toEqual(['Trim from 1.0s to 3.5s']);
      expect(videoEntries).toEqual(['Trim from 0.5s to the end']);
    });

    it('appends output file naming text when provided', () => {
      const entries = describeSelectedOptions('image', {
        ...baseOptions,
        outputBaseName: '  final-export ',
      });

      expect(entries).toEqual(['Save as final-export']);
    });

    it('ignores image resize entries for non-image media kinds', () => {
      const entries = describeSelectedOptions('audio', {
        ...baseOptions,
        image: {
          width: 1024,
          height: 1024,
          keepAspectRatio: true,
        },
      });

      expect(entries).toEqual([]);
    });
  });
});
