declare module 'gifenc' {
  export type GIFEncoderStream = {
    readonly buffer: ArrayBuffer;
    reset(): void;
    bytesView(): Uint8Array;
    bytes(): Uint8Array;
    writeByte(value: number): void;
    writeBytes(data: Uint8Array | number[], offset?: number, length?: number): void;
    writeBytesView(data: Uint8Array, offset?: number, length?: number): void;
  };

  export type GIFEncoderInstance = {
    reset(): void;
    finish(): void;
    bytes(): Uint8Array;
    bytesView(): Uint8Array;
    readonly buffer: ArrayBuffer;
    readonly stream: GIFEncoderStream;
    writeHeader(): void;
    writeFrame(
      index: Uint8Array,
      width: number,
      height: number,
      options?: {
        transparent?: boolean;
        transparentIndex?: number;
        delay?: number;
        palette?: number[][];
        repeat?: number;
        colorDepth?: number;
        dispose?: number;
        first?: boolean;
      },
    ): void;
  };

  export function GIFEncoder(options?: {
    initialCapacity?: number;
    auto?: boolean;
  }): GIFEncoderInstance;

  export function quantize(
    data: Uint8Array | Uint8ClampedArray,
    maxColors: number,
    options?: {
      format?: 'rgb565' | 'rgb444' | 'rgba4444';
      clearAlpha?: boolean;
      clearAlphaColor?: number;
      clearAlphaThreshold?: number;
      oneBitAlpha?: boolean | number;
      useSqrt?: boolean;
    },
  ): number[][];

  export function applyPalette(
    data: Uint8Array | Uint8ClampedArray,
    palette: number[][],
    format?: 'rgb565' | 'rgb444' | 'rgba4444',
  ): Uint8Array;

  export function prequantize(
    data: Uint8Array | Uint8ClampedArray,
    options?: {
      roundRGB?: number;
      roundAlpha?: number;
      oneBitAlpha?: boolean | number;
    },
  ): void;

  export function nearestColorIndex(
    palette: number[][],
    color: number[],
    distanceFn?: (a: number[], b: number[]) => number,
  ): number;

  export function nearestColor(
    palette: number[][],
    color: number[],
    distanceFn?: (a: number[], b: number[]) => number,
  ): number[];

  export function nearestColorIndexWithDistance(
    palette: number[][],
    color: number[],
    distanceFn?: (a: number[], b: number[]) => number,
  ): [number, number];

  export function snapColorsToPalette(
    palette: number[][],
    knownColors: number[][],
    threshold?: number,
  ): void;

  export default GIFEncoder;
}
