import styles from './mark.module.css';

/** Static theme-owned brand mark rendered in currentColor. */
export function BrandMark({ className = '' }: Readonly<{ className?: string }>) {
  return (
    <svg
      className={`${styles.mark} ${className}`}
      viewBox="0 0 384 384"
      fill="currentColor"
      aria-hidden="true"
      focusable="false"
      data-nc-brand-mark=""
    >
      <path d="M50 54 H334 V70 H50 Z" />
      <path d="M58 80 H326 V100 H58 Z" />
      <path d="M62 110 H114 V122 H62 Z M270 110 H322 V122 H270 Z" />
      <path d="M122 110 H262 V200 H252 A60 60 0 0 0 132 200 H122 Z" />
      <path d="M114 208 H140 V218 H114 Z M244 208 H270 V218 H244 Z" />
      <path d="M118 226 H136 V330 H118 Z M248 226 H266 V330 H248 Z" />
      <path d="M72 132 H104 L110 256 H66 Z M280 132 H312 L318 256 H274 Z" />
      <path d="M62 266 H112 V308 H62 Z M272 266 H322 V308 H272 Z" />
      <path d="M52 318 H120 V330 H52 Z M264 318 H332 V330 H264 Z" />
    </svg>
  );
}
