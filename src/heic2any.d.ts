declare module 'heic2any' {
  type Heic2AnyOptions = {
    blob: Blob;
    toType?: string;
    quality?: number;
  };

  export default function heic2any(options: Heic2AnyOptions): Promise<Blob | Blob[]>;
}
