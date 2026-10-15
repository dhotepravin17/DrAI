import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { useClinicScope } from './clinicScopeState'

/**
 * PRD FR-07: each doctor profile has a shareable URL and a printable QR code for the clinic front desk.
 * Patients scan it to open the profile and book an appointment.
 */
export function ProfileSharePage() {
  const { current } = useClinicScope()
  const code = current.doctorCode
  const url = code ? `${window.location.origin}/doctors/${code}` : null
  const [qr, setQr] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    if (!url) return
    let cancelled = false
    // Loaded on demand so the QR library is not part of every page's bundle.
    import('qrcode')
      .then(({ default: QRCode }) =>
        QRCode.toString(url, { type: 'svg', margin: 1, errorCorrectionLevel: 'M', color: { dark: '#000000', light: '#ffffff' } }))
      .then((svg) => { if (!cancelled) setQr(svg) })
      .catch(() => { if (!cancelled) setQr(null) })
    return () => { cancelled = true }
  }, [url])

  async function copy() {
    if (!url) return
    try {
      await navigator.clipboard.writeText(url)
      setCopied(true)
    } catch {
      setCopied(false)
    }
  }

  if (!code || !url) return <p className="panel">Your Doctor ID is issued when your registration is verified.</p>

  return (
    <section className="stack">
      <title>Profile and QR code · Smart Doctor Queue</title>
      <h1 className="no-print">Profile and QR code</h1>
      <div className="panel no-print">
        <h2>Your public profile</h2>
        <p>Share this link with patients. It shows your clinic hours, today's appointments and notices, and lets patients book an appointment.</p>
        <p className="code share-url">{url}</p>
        <div className="actions">
          <button type="button" className="button" onClick={() => void copy()}>{copied ? 'Link copied' : 'Copy link'}</button>
          <Link to={`/doctors/${code}`} className="button">Open profile</Link>
          <button type="button" className="button primary" onClick={() => window.print()} disabled={!qr}>Print QR poster</button>
        </div>
      </div>

      <div className="panel qr-poster">
        <p className="qr-title">Scan to book an appointment</p>
        <p className="qr-doctor">{current.doctorName}</p>
        <p className="meta">{current.clinicName} · Doctor ID <span className="code">{code}</span></p>
        {qr ? (
          // The SVG is generated locally from our own URL by the qrcode library.
          <div className="qr-code" role="img" aria-label={`QR code for ${url}`} dangerouslySetInnerHTML={{ __html: qr }} />
        ) : (
          <p className="hint">Preparing QR code…</p>
        )}
        <p className="hint">Or open {url}</p>
      </div>
    </section>
  )
}
