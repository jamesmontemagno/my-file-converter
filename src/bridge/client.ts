import type { MediaKind } from '../capabilities';
import type { ConversionOptions } from '../conversion-options';

export type BridgeTargetMime =
  | 'video/mp4'
  | 'video/quicktime'
  | 'video/webm'
  | 'image/gif'
  | 'audio/mpeg'
  | 'audio/wav';

export type BridgeConnection = {
  baseUrl: string;
  token: string;
};

export type BridgeHealth = {
  version: string;
  ffmpeg: {
    available: boolean;
    version?: string;
  };
  supportedTargets: string[];
};

export type BridgeJobEvent = {
  status: 'queued' | 'running' | 'completed' | 'failed' | 'canceled';
  progress: number;
  message: string;
  detail?: string;
  rawOutput?: string;
};

export class BridgeError extends Error {
  constructor(
    message: string,
    readonly code: 'connection' | 'authentication' | 'ffmpeg-missing' | 'job' | 'protocol',
  ) {
    super(message);
    this.name = 'BridgeError';
  }
}

export function normalizeBridgeProgress(progress: number) {
  return Math.max(0, Math.min(1, progress / 100));
}

const bridgeExtensions: Record<BridgeTargetMime, string> = {
  'video/mp4': 'mp4',
  'video/quicktime': 'mov',
  'video/webm': 'webm',
  'image/gif': 'gif',
  'audio/mpeg': 'mp3',
  'audio/wav': 'wav',
};

const directBridgeTargets = new Set<BridgeTargetMime>(Object.keys(bridgeExtensions) as BridgeTargetMime[]);

/**
 * Converts app MIME choices to an exact bridge target when their codecs match
 * the bridge's fixed FFmpeg output. Unsupported codec-qualified choices return
 * null rather than silently changing the requested output codec.
 */
export function normalizeBridgeTargetMime(targetMime: string): BridgeTargetMime | null {
  const [rawType, ...rawParameters] = targetMime.toLowerCase().split(';');
  const mimeType = rawType.trim();
  const parameters = new Map(
    rawParameters
      .map((parameter) => parameter.trim().split('=', 2))
      .filter(([name, value]) => name && value)
      .map(([name, value]) => [name.trim(), value.trim().replace(/^"|"$/g, '')]),
  );

  if (parameters.size === 0 && directBridgeTargets.has(mimeType as BridgeTargetMime)) {
    return mimeType as BridgeTargetMime;
  }

  if (parameters.size !== 1 || parameters.get('codecs') === undefined) {
    return null;
  }

  const codecs = parameters
    .get('codecs')!
    .split(',')
    .map((codec) => codec.trim())
    .sort()
    .join(',');

  if (mimeType === 'video/mp4' && codecs === 'avc1.42e01e,mp4a.40.2') {
    return 'video/mp4';
  }

  if (mimeType === 'video/webm' && codecs === 'opus,vp9') {
    return 'video/webm';
  }

  return null;
}

export function normalizeBridgeQuality(quality: number) {
  return Math.min(100, Math.max(1, Math.round(quality * 100)));
}

function bridgeDimension(value: number | null) {
  if (!Number.isFinite(value)) return undefined;
  return Math.min(16_384, Math.max(1, Math.round(value!)));
}

function safeBridgeOutputName(fileName: string, outputBaseName: string, targetMime: BridgeTargetMime) {
  const sourceBaseName = fileName.replace(/^.*[\\/]/, '').replace(/\.[^.]*$/, '');
  const requestedBaseName = outputBaseName.trim() || sourceBaseName || 'converted';
  const safeBaseName = requestedBaseName.replace(/[^A-Za-z0-9._-]+/g, '-').slice(0, 124);
  const extension = bridgeExtensions[targetMime];
  const maximumBaseLength = 128 - extension.length - 1;
  return `${(safeBaseName || 'converted').slice(0, maximumBaseLength)}.${extension}`;
}

export type BridgeConversionRequest = {
  targetMime: BridgeTargetMime;
  outputName: string;
  mediaType: Exclude<MediaKind, 'unknown'>;
  quality: number;
  image: {
    width?: number;
    height?: number;
    keepAspectRatio: boolean;
  };
  media: {
    trimStart?: number;
    trimEnd?: number;
    channelMode: 'source' | 'mono' | 'stereo';
    videoEncodingSpeed?: 'fast' | 'balanced' | 'quality';
    videoFrameRate?: number;
    audioBitrate?: number;
    audioSampleRate?: number;
    wavBitDepth?: 16 | 24 | 32;
  };
};

function bridgeMediaType(targetMime: BridgeTargetMime): Exclude<MediaKind, 'unknown'> {
  if (targetMime === 'image/gif') return 'image';
  if (targetMime === 'audio/mpeg' || targetMime === 'audio/wav') return 'audio';
  return 'video';
}

export function normalizeBridgeRequest(args: {
  fileName: string;
  targetMime: string;
  mediaType: MediaKind;
  quality: number;
  options: ConversionOptions;
}): BridgeConversionRequest {
  const targetMime = normalizeBridgeTargetMime(args.targetMime);
  if (!targetMime) {
    throw new BridgeError(`LocalMorph Bridge cannot produce ${args.targetMime}.`, 'protocol');
  }

  const trimStart =
    Number.isFinite(args.options.media.trimStart) && args.options.media.trimStart > 0
      ? args.options.media.trimStart
      : undefined;
  const trimEnd =
    Number.isFinite(args.options.media.trimEnd) &&
    args.options.media.trimEnd > (trimStart ?? 0)
      ? args.options.media.trimEnd
      : undefined;
  const imageWidth = bridgeDimension(args.options.image.width);
  const imageHeight = bridgeDimension(args.options.image.height);
  const videoFrameRate =
    targetMime.startsWith('video/') && [24, 30, 60].includes(args.options.media.videoFrameRate ?? 0)
      ? args.options.media.videoFrameRate ?? undefined
      : undefined;
  const audioBitrate =
    (targetMime.startsWith('video/') || targetMime === 'audio/mpeg') &&
    [64, 96, 128, 192, 256, 320].includes(args.options.media.audioBitrate ?? 0)
      ? args.options.media.audioBitrate ?? undefined
      : undefined;
  const audioSampleRate =
    (targetMime.startsWith('video/') || targetMime === 'audio/mpeg' || targetMime === 'audio/wav') &&
    [22050, 44100, 48000].includes(args.options.media.audioSampleRate ?? 0)
      ? args.options.media.audioSampleRate ?? undefined
      : undefined;
  const wavBitDepth =
    targetMime === 'audio/wav' && [16, 24, 32].includes(args.options.media.wavBitDepth)
      ? args.options.media.wavBitDepth
      : undefined;

  return {
    targetMime,
    outputName: safeBridgeOutputName(args.fileName, args.options.outputBaseName, targetMime),
    mediaType: bridgeMediaType(targetMime),
    quality: normalizeBridgeQuality(args.quality),
    image: {
      ...(imageWidth === undefined ? {} : { width: imageWidth }),
      ...(imageHeight === undefined ? {} : { height: imageHeight }),
      keepAspectRatio: args.options.image.keepAspectRatio,
    },
    media: {
      ...(trimStart === undefined ? {} : { trimStart }),
      ...(trimEnd === undefined ? {} : { trimEnd }),
      channelMode: args.options.media.channelMode === 'auto' ? 'source' : args.options.media.channelMode,
      ...(targetMime.startsWith('video/') ? { videoEncodingSpeed: args.options.media.videoEncodingSpeed } : {}),
      ...(videoFrameRate === undefined ? {} : { videoFrameRate }),
      ...(audioBitrate === undefined ? {} : { audioBitrate }),
      ...(audioSampleRate === undefined ? {} : { audioSampleRate }),
      ...(wavBitDepth === undefined ? {} : { wavBitDepth }),
    },
  };
}

function errorMessage(response: Response, fallback: string) {
  const detail = response.statusText.trim();
  return detail ? `${fallback}: ${detail}` : fallback;
}

export function normalizeBridgeConnection(baseUrl: string, token: string): BridgeConnection {
  let url: URL;

  try {
    url = new URL(baseUrl.trim());
  } catch {
    throw new BridgeError('Enter the bridge URL shown when LocalMorph Bridge starts.', 'connection');
  }

  const localHosts = new Set(['127.0.0.1', 'localhost', '[::1]']);
  if (url.protocol !== 'http:' || !localHosts.has(url.hostname) || !url.port || url.pathname !== '/') {
    throw new BridgeError('LocalMorph Bridge must use an http:// loopback URL with a port.', 'connection');
  }

  const normalizedToken = token.trim();
  if (!normalizedToken) {
    throw new BridgeError('Enter the pairing token shown by LocalMorph Bridge.', 'authentication');
  }

  return {
    baseUrl: url.origin,
    token: normalizedToken,
  };
}

export function parseBridgeStartupLine(value: string): BridgeConnection {
  const prefix = 'LOCALMORPH_BRIDGE=';
  const trimmed = value.trim();
  const json = trimmed.startsWith(prefix) ? trimmed.slice(prefix.length) : trimmed;

  let payload: unknown;
  try {
    payload = JSON.parse(json);
  } catch {
    throw new BridgeError('Paste the complete LOCALMORPH_BRIDGE startup line.', 'connection');
  }

  if (
    typeof payload !== 'object' ||
    payload === null ||
    !('baseUrl' in payload) ||
    !('token' in payload) ||
    typeof payload.baseUrl !== 'string' ||
    typeof payload.token !== 'string'
  ) {
    throw new BridgeError('The bridge startup line is missing its URL or pairing token.', 'connection');
  }

  return normalizeBridgeConnection(payload.baseUrl, payload.token);
}

function headers(connection: BridgeConnection, headersInit?: HeadersInit) {
  return {
    ...headersInit,
    Authorization: `Bearer ${connection.token}`,
  };
}

async function expectOk(response: Response, fallback: string) {
  if (response.ok) return response;

  if (response.status === 401 || response.status === 403) {
    throw new BridgeError('The LocalMorph Bridge pairing token was rejected.', 'authentication');
  }

  throw new BridgeError(errorMessage(response, fallback), 'connection');
}

export async function probeBridge(connection: BridgeConnection, signal?: AbortSignal): Promise<BridgeHealth> {
  let response: Response;
  try {
    response = await fetch(`${connection.baseUrl}/v1/health`, {
      headers: headers(connection),
      signal,
    });
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error;
    throw new BridgeError(
      'Could not reach LocalMorph Bridge. Start it, then copy its URL and pairing token here.',
      'connection',
    );
  }

  await expectOk(response, 'Could not check LocalMorph Bridge');
  let health: BridgeHealth;
  try {
    health = (await response.json()) as BridgeHealth;
  } catch {
    throw new BridgeError('LocalMorph Bridge returned an invalid health response.', 'protocol');
  }

  if (!health.ffmpeg?.available) {
    throw new BridgeError(
      'LocalMorph Bridge is running, but FFmpeg was not found on its PATH. Install FFmpeg, then restart the bridge.',
      'ffmpeg-missing',
    );
  }

  if (!Array.isArray(health.supportedTargets)) {
    throw new BridgeError('LocalMorph Bridge returned an invalid capability response.', 'protocol');
  }

  return health;
}

type CreateBridgeJobArgs = {
  connection: BridgeConnection;
  file: File;
  targetMime: string;
  mediaType: MediaKind;
  quality: number;
  options: ConversionOptions;
  signal?: AbortSignal;
};

async function createBridgeJob(args: CreateBridgeJobArgs) {
  const request = normalizeBridgeRequest({
    fileName: args.file.name,
    targetMime: args.targetMime,
    mediaType: args.mediaType,
    quality: args.quality,
    options: args.options,
  });
  const body = new FormData();
  body.append('file', args.file, args.file.name);
  body.append('request', JSON.stringify(request));

  let response: Response;
  try {
    response = await fetch(`${args.connection.baseUrl}/v1/jobs`, {
      method: 'POST',
      headers: headers(args.connection),
      body,
      signal: args.signal,
    });
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error;
    throw new BridgeError('Could not send the file to LocalMorph Bridge.', 'connection');
  }

  await expectOk(response, 'LocalMorph Bridge could not create this conversion job');
  const payload = (await response.json()) as { id?: unknown };
  if (typeof payload.id !== 'string' || !payload.id) {
    throw new BridgeError('LocalMorph Bridge returned an invalid job response.', 'protocol');
  }

  return payload.id;
}

async function* streamBridgeEvents(
  connection: BridgeConnection,
  jobId: string,
  signal?: AbortSignal,
): AsyncGenerator<BridgeJobEvent> {
  let response: Response;
  try {
    response = await fetch(`${connection.baseUrl}/v1/jobs/${encodeURIComponent(jobId)}/events`, {
      headers: headers(connection, { Accept: 'text/event-stream' }),
      signal,
    });
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error;
    throw new BridgeError('Lost the LocalMorph Bridge progress connection.', 'connection');
  }

  await expectOk(response, 'Could not subscribe to LocalMorph Bridge progress');
  if (!response.body) {
    throw new BridgeError('LocalMorph Bridge did not provide a progress stream.', 'protocol');
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';

  try {
    while (true) {
      const { done, value } = await reader.read();
      buffer += decoder.decode(value, { stream: !done });

      let boundaryMatch = buffer.match(/\r?\n\r?\n/);
      while (boundaryMatch?.index !== undefined) {
        const boundary = boundaryMatch.index;
        const eventBlock = buffer.slice(0, boundary);
        buffer = buffer.slice(boundary + boundaryMatch[0].length);
        boundaryMatch = buffer.match(/\r?\n\r?\n/);
        const data = eventBlock
          .split(/\r?\n/)
          .filter((line) => line.startsWith('data:'))
          .map((line) => line.slice(5).trimStart())
          .join('\n');
        if (!data) continue;

        let event: BridgeJobEvent;
        try {
          event = JSON.parse(data) as BridgeJobEvent;
        } catch {
          throw new BridgeError('LocalMorph Bridge sent an invalid progress event.', 'protocol');
        }

        if (
          !['queued', 'running', 'completed', 'failed', 'canceled'].includes(event.status) ||
          typeof event.progress !== 'number' ||
          typeof event.message !== 'string'
        ) {
          throw new BridgeError('LocalMorph Bridge sent an incomplete progress event.', 'protocol');
        }

        yield event;
      }

      if (done) break;
    }
  } finally {
    reader.releaseLock();
  }
}

async function downloadBridgeOutput(
  connection: BridgeConnection,
  jobId: string,
  targetMime: string,
  signal?: AbortSignal,
) {
  const response = await fetch(`${connection.baseUrl}/v1/jobs/${encodeURIComponent(jobId)}/output`, {
    headers: headers(connection),
    signal,
  });
  await expectOk(response, 'Could not download the LocalMorph Bridge output');
  const output = await response.blob();
  return new Blob([output], { type: targetMime });
}

export async function cancelBridgeJob(connection: BridgeConnection, jobId: string) {
  const response = await fetch(`${connection.baseUrl}/v1/jobs/${encodeURIComponent(jobId)}`, {
    method: 'DELETE',
    headers: headers(connection),
  });
  await expectOk(response, 'Could not cancel the LocalMorph Bridge job');
}

export async function convertThroughBridge(args: CreateBridgeJobArgs & {
  onProgress: (event: BridgeJobEvent) => void;
}) {
  const jobId = await createBridgeJob(args);
  let terminalEvent: BridgeJobEvent | undefined;

  const abortHandler = () => {
    void cancelBridgeJob(args.connection, jobId).catch(() => undefined);
  };
  args.signal?.addEventListener('abort', abortHandler, { once: true });

  try {
    for await (const event of streamBridgeEvents(args.connection, jobId, args.signal)) {
      const isTerminal =
        event.status === 'completed' || event.status === 'failed' || event.status === 'canceled';
      if (isTerminal) {
        terminalEvent = event;
      }
      args.onProgress(event);
      if (isTerminal) break;
    }
  } finally {
    args.signal?.removeEventListener('abort', abortHandler);
    if (!terminalEvent) {
      await cancelBridgeJob(args.connection, jobId).catch(() => undefined);
    }
  }

  if (args.signal?.aborted) {
    throw new DOMException('Conversion canceled.', 'AbortError');
  }

  if (!terminalEvent) {
    throw new BridgeError('LocalMorph Bridge closed the progress stream before the job completed.', 'protocol');
  }

  if (terminalEvent.status === 'canceled') {
    throw new DOMException('Conversion canceled.', 'AbortError');
  }

  if (terminalEvent.status === 'failed') {
    throw new BridgeError(terminalEvent.detail || terminalEvent.message, 'job');
  }

  return downloadBridgeOutput(
    args.connection,
    jobId,
    normalizeBridgeTargetMime(args.targetMime) ?? args.targetMime,
    args.signal,
  );
}
