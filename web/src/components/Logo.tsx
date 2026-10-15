import { useId } from 'react'

/** The Valmedo mark: a medical cross with a pulse line. Matches public/favicon.svg. */
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
        d="M26 11h12a2 2 0 0 1 2 2v11h11a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H40v11a2 2 0 0 1-2 2H26a2 2 0 0 1-2-2V40H13a2 2 0 0 1-2-2V26a2 2 0 0 1 2-2h11V13a2 2 0 0 1 2-2z"
        fill="#ffffff"
      />
      <path d="M14 32.5h9l3.5-6 5 12.5 4-8.5 2.5 2H50" fill="none" stroke="#0f766e" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}

/** Mark plus the wordmark "Valmedo" with the "Appointment" tagline. */
export function Logo({ size = 34 }: { size?: number }) {
  return (
    <>
      <LogoMark size={size} />
      <span className="brand-name">
        <span className="brand-word">Valmedo</span>
        <span className="brand-tag">Appointment</span>
      </span>
    </>
  )
}
