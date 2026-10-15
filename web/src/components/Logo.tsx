import { useId } from 'react'

/** The MediCare Plus mark: a medical cross with a pulse line, and a small "plus" accent. Matches public/favicon.svg. */
export function LogoMark({ size = 32 }: { size?: number }) {
  const gradient = useId()
  return (
    <svg className="brand-mark" width={size} height={size} viewBox="0 0 64 64" aria-hidden="true" focusable="false">
      <defs>
        <linearGradient id={gradient} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="#14b8a6" />
          <stop offset="1" stopColor="#0f766e" />
        </linearGradient>
      </defs>
      <rect width="64" height="64" rx="16" fill={`url(#${gradient})`} />
      <path
        d="M26 13h12a2 2 0 0 1 2 2v9h9a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-9v9a2 2 0 0 1-2 2H26a2 2 0 0 1-2-2v-9h-9a2 2 0 0 1-2-2V26a2 2 0 0 1 2-2h9v-9a2 2 0 0 1 2-2z"
        fill="#ffffff"
      />
      <path d="M16 32.5h8l3.5-6 5 12.5 4-8.5 2.5 2H48" fill="none" stroke="#0f766e" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M53 5.5v9M48.5 10h9" stroke="#ccfbf1" strokeWidth="3.2" strokeLinecap="round" />
    </svg>
  )
}

/** Mark plus the two-tone wordmark "MediCare Plus". */
export function Logo({ size = 32 }: { size?: number }) {
  return (
    <>
      <LogoMark size={size} />
      <span className="brand-name">MediCare <span className="brand-plus">Plus</span></span>
    </>
  )
}
