declare module 'lamejs' {
  export class Mp3Encoder {
    constructor(channels: number, sampleRate: number, kbps: number);
    encodeBuffer(left: Int16Array, right?: Int16Array): Int8Array;
    flush(): Int8Array;
  }
}

declare module 'lamejs/src/js/MPEGMode.js' {
  const MPEGMode: {
    STEREO: unknown;
    JOINT_STEREO: unknown;
    DUAL_CHANNEL: unknown;
    MONO: unknown;
    NOT_SET: unknown;
  };

  export default MPEGMode;
}

declare module 'lamejs/src/js/Lame.js' {
  const Lame: unknown;
  export default Lame;
}
