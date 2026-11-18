import { describe, expect, it } from 'vitest';

import { detectDesktopPlatform } from './desktop-downloads';

describe('detectDesktopPlatform', () => {
  const signals = { platform: '', userAgent: '', maxTouchPoints: 0 };

  it('prefers the browser platform hint over the legacy platform', () => {
    expect(detectDesktopPlatform({
      ...signals,
      platform: 'Linux x86_64',
      userAgentData: { platform: 'Windows', mobile: false },
    })).toBe('windows');
  });

  it('does not present a desktop download as primary when the browser reports mobile', () => {
    expect(detectDesktopPlatform({
      ...signals,
      userAgentData: { platform: 'macOS', mobile: true },
    })).toBeNull();
  });

  it.each([
    ['Win32', 'windows'],
    ['MacIntel', 'mac'],
    ['Linux x86_64', null],
    ['', null],
  ] as const)('uses the legacy desktop platform %j when hints are absent', (platform, expected) => {
    expect(detectDesktopPlatform({ ...signals, platform })).toBe(expected);
  });

  it('recognizes macOS from the user agent when platform fields are unavailable', () => {
    expect(detectDesktopPlatform({
      ...signals,
      userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15',
    })).toBe('mac');
  });

  it('does not present a Mac download as primary on iPadOS in desktop mode', () => {
    expect(detectDesktopPlatform({
      ...signals,
      platform: 'MacIntel',
      userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15',
      maxTouchPoints: 5,
    })).toBeNull();
  });

  it('excludes Windows Phone even when its user agent contains a Windows desktop token', () => {
    expect(detectDesktopPlatform({
      ...signals,
      platform: 'Win32',
      userAgent: 'Mozilla/5.0 (Windows Phone 10.0; Android 6.0.1)',
    })).toBeNull();
  });
});
