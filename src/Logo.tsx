export function LogoIcon({ size = 24 }: { size?: number }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 512 512"
      width={size}
      height={size}
      aria-hidden="true"
    >
      <defs>
        <linearGradient id="li-bg" x1="0" y1="0" x2="512" y2="512" gradientUnits="userSpaceOnUse">
          <stop offset="0%" stopColor="#0f172a" />
          <stop offset="100%" stopColor="#1e293b" />
        </linearGradient>
        <linearGradient
          id="li-ag"
          x1="100"
          y1="140"
          x2="412"
          y2="372"
          gradientUnits="userSpaceOnUse"
        >
          <stop offset="0%" stopColor="#38bdf8" />
          <stop offset="100%" stopColor="#818cf8" />
        </linearGradient>
      </defs>
      <rect width="512" height="512" rx="96" fill="url(#li-bg)" />
      {/* Top arrow right */}
      <line
        x1="128"
        y1="190"
        x2="332"
        y2="190"
        stroke="url(#li-ag)"
        strokeWidth="42"
        strokeLinecap="round"
      />
      <polyline
        points="292,148 352,190 292,232"
        stroke="url(#li-ag)"
        strokeWidth="42"
        fill="none"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
      {/* Bottom arrow left */}
      <line
        x1="384"
        y1="322"
        x2="180"
        y2="322"
        stroke="url(#li-ag)"
        strokeWidth="42"
        strokeLinecap="round"
      />
      <polyline
        points="220,280 160,322 220,364"
        stroke="url(#li-ag)"
        strokeWidth="42"
        fill="none"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}
