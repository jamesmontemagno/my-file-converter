export const desktopDownloads = {
  windows: {
    name: 'Windows',
    store: 'Microsoft Store',
    url: 'https://apps.microsoft.com/detail/9PM1NHPT1JR5',
    requirements: 'Windows 10 version 2004 or later.',
    setup: "Install FFmpeg and FFprobe separately using the app's Tools page.",
    engine: 'Powered by your local FFmpeg installation',
  },
  mac: {
    name: 'Mac',
    store: 'Mac App Store',
    url: 'https://apps.apple.com/us/app/localmorph/id6824266726',
    requirements: 'macOS 15 or later on Apple Silicon.',
    setup: 'FFmpeg and FFprobe are included.',
    engine: "Powered by the app's bundled FFmpeg",
  },
};

type DesktopPlatform = keyof typeof desktopDownloads;

type PlatformSignals = Pick<Navigator, 'platform' | 'userAgent' | 'maxTouchPoints'> & {
  userAgentData?: { platform?: string; mobile?: boolean };
};

export function detectDesktopPlatform(nav: PlatformSignals): DesktopPlatform | null {
  if (nav.userAgentData?.mobile || /\b(Android|iPhone|iPad|iPod|Windows Phone)\b/i.test(nav.userAgent)) {
    return null;
  }

  const platform = nav.userAgentData?.platform || nav.platform || nav.userAgent;
  if (/\b(Windows|Win32|Win64)\b/i.test(platform)) return 'windows';

  // iPadOS desktop mode reports itself as Mac but still exposes multitouch.
  if (/\b(macOS|MacIntel|MacPPC|Macintosh|Mac OS)\b/i.test(platform) && nav.maxTouchPoints <= 1) {
    return 'mac';
  }
  return null;
}
