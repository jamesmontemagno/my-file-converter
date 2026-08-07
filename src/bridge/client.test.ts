import { describe, expect, it } from 'vitest';

import {
  BridgeError,
  normalizeBridgeConnection,
  normalizeBridgeQuality,
  normalizeBridgeRequest,
  normalizeBridgeTargetMime,
} from './client';

const defaultOptions = {
  outputBaseName: '',
  image: {
    width: null,
    height: null,
    keepAspectRatio: true,
  },
  media: {
    trimStart: 0,
    trimEnd: 0,
    channelMode: 'auto' as const,
  },
};

describe('normalizeBridgeConnection', () => {
  it('accepts loopback bridge details and removes a trailing slash', () => {
    expect(normalizeBridgeConnection('http://127.0.0.1:49321/', ' bridge-token ')).toEqual({
      baseUrl: 'http://127.0.0.1:49321',
      token: 'bridge-token',
    });
  });

  it('rejects non-loopback and malformed bridge URLs', () => {
    expect(() => normalizeBridgeConnection('https://localmorph.com', 'token')).toThrow(BridgeError);
    expect(() => normalizeBridgeConnection('http://192.168.1.5:49321', 'token')).toThrow(BridgeError);
    expect(() => normalizeBridgeConnection('not a url', 'token')).toThrow(BridgeError);
  });

  it('requires a pairing token', () => {
    expect(() => normalizeBridgeConnection('http://localhost:49321', '   ')).toThrow(BridgeError);
  });
});

describe('normalizeBridgeTargetMime', () => {
  it('maps only codec-qualified app targets emitted by the bridge', () => {
    expect(normalizeBridgeTargetMime('video/mp4;codecs=avc1.42E01E,mp4a.40.2')).toBe('video/mp4');
    expect(normalizeBridgeTargetMime('video/webm;codecs=vp9,opus')).toBe('video/webm');
    expect(normalizeBridgeTargetMime('video/webm;codecs=vp8,opus')).toBeNull();
    expect(normalizeBridgeTargetMime('video/webm;codecs=av01')).toBeNull();
  });

  it('keeps directly supported bridge targets', () => {
    expect(normalizeBridgeTargetMime('audio/mpeg')).toBe('audio/mpeg');
    expect(normalizeBridgeTargetMime('image/gif')).toBe('image/gif');
  });
});

describe('normalizeBridgeRequest', () => {
  it('converts app options to the bridge contract', () => {
    expect(
      normalizeBridgeRequest({
        fileName: 'source video.mov',
        targetMime: 'video/webm;codecs=vp9,opus',
        mediaType: 'video',
        quality: 0.9,
        options: {
          ...defaultOptions,
          outputBaseName: '../My final export',
          image: { width: null, height: 20_000, keepAspectRatio: false },
          media: { trimStart: 2.5, trimEnd: 0, channelMode: 'auto' },
        },
      }),
    ).toEqual({
      targetMime: 'video/webm',
      outputName: '..-My-final-export.webm',
      mediaType: 'video',
      quality: 90,
      image: { height: 16_384, keepAspectRatio: false },
      media: { trimStart: 2.5, channelMode: 'source' },
    });
  });

  it('omits unset image dimensions so GIFs retain their original size', () => {
    expect(
      normalizeBridgeRequest({
        fileName: 'source.gif',
        targetMime: 'image/gif',
        mediaType: 'image',
        quality: 0.8,
        options: defaultOptions,
      }).image,
    ).toEqual({ keepAspectRatio: true });
  });

  it('uses the target media type and always supplies a safe filename extension', () => {
    expect(
      normalizeBridgeRequest({
        fileName: 'recording.avi',
        targetMime: 'audio/mpeg',
        mediaType: 'video',
        quality: 0.1,
        options: defaultOptions,
      }),
    ).toMatchObject({
      mediaType: 'audio',
      outputName: 'recording.mp3',
      quality: 10,
      media: { channelMode: 'source' },
    });
  });
});

describe('normalizeBridgeQuality', () => {
  it('scales browser quality values to the bridge 1-100 range', () => {
    expect(normalizeBridgeQuality(0.1)).toBe(10);
    expect(normalizeBridgeQuality(1)).toBe(100);
  });
});
