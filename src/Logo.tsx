export function LogoIcon({ size = 24 }: { size?: number }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 512 512"
      width={size}
      height={size}
      aria-hidden="true"
    >
      <rect width="512" height="512" rx="88" fill="#0B172A" />
      <circle cx="118" cy="256" r="25" fill="#FFFFFF" />
      <path d="M143 256H196" stroke="#FFFFFF" strokeWidth="30" strokeLinecap="round" />
      <rect x="196" y="196" width="120" height="120" rx="18" fill="#1356D8" transform="rotate(45 256 256)" />
      <rect x="228" y="228" width="56" height="56" rx="9" fill="#0B172A" transform="rotate(45 256 256)" />
      <path d="M316 256H386" stroke="#9BC53D" strokeWidth="30" strokeLinecap="round" />
      <path d="M368 208L428 256L368 304" fill="none" stroke="#9BC53D" strokeWidth="30" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}
