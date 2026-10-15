import { Link, useParams } from 'react-router-dom'
import { ApiError, errorMessage } from '../api/client'
import type { Availability, Notice } from '../api/queueTypes'
import { announcementLabels, type AnnouncementType } from '../api/queueTypes'
import type { DoctorCard } from '../api/types'
import { useApi } from '../api/useApi'
import { Loading } from '../components/Layout'
import { formatDay, formatTime } from '../format'

export function NoticeList({ notices }: { notices: Notice[] }) {
  if (notices.length === 0) return null
  return (
    <ul className="notice-list" aria-label="Clinic notices">
      {notices.map((n) => (
        <li key={`${n.type}-${n.startsAt}-${n.message}`} className="alert alert-warning">
          <strong>{announcementLabels[n.type as AnnouncementType] ?? (n.type === 'QueuePaused' ? 'Appointments paused' : n.type)}:</strong>{' '}
          {n.message}
          {n.endsAt && <span className="hint"> (until {formatDay(n.endsAt.slice(0, 10))} {formatTime(n.endsAt)})</span>}
        </li>
      ))}
    </ul>
  )
}

export function DoctorProfilePage() {
  const { code = '' } = useParams()
  const { data: doctor, error, loading } = useApi<DoctorCard>(`/doctors/${encodeURIComponent(code)}`)
  const availability = useApi<Availability>(doctor ? `/doctors/${encodeURIComponent(code)}/availability` : null)

  if (loading && !doctor) return <Loading />
  if (error || !doctor) {
    const missing = error instanceof ApiError && error.status === 404
    return (
      <section className="panel">
        <title>Doctor not found · Smart Doctor</title>
        <h1>{missing ? 'Doctor not found' : 'Could not load this doctor'}</h1>
        <p>{missing ? `No active doctor has the ID ${code}. The doctor may not be verified yet.` : errorMessage(error)}</p>
        <Link to="/" className="button">Back to search</Link>
      </section>
    )
  }

  const days = availability.data?.days ?? []
  const today = days[0]

  return (
    <article className="stack">
      <title>{`${doctor.fullName} · Smart Doctor`}</title>
      <section className="panel profile">
        <header className="profile-header">
          <div>
            <h1>{doctor.fullName}</h1>
            <p className="meta">{doctor.specialty}{doctor.qualifications ? ` · ${doctor.qualifications}` : ''}</p>
          </div>
          {doctor.isVerified && <span className="badge badge-verified">✓ Verified</span>}
        </header>
        <dl className="details">
          <div><dt>Doctor ID</dt><dd className="code">{doctor.doctorCode}</dd></div>
          <div><dt>City</dt><dd>{doctor.cityName}</dd></div>
          {doctor.clinic && (
            <>
              <div><dt>Clinic</dt><dd>{doctor.clinic.name}</dd></div>
              <div>
                <dt>Address</dt>
                <dd>
                  {doctor.clinic.address}{' '}
                  <a href={`https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(`${doctor.clinic.name}, ${doctor.clinic.address}, ${doctor.cityName}`)}`}
                    target="_blank" rel="noreferrer">Map</a>
                </dd>
              </div>
              {doctor.clinic.publicPhone && (
                <div>
                  <dt>Clinic phone</dt>
                  <dd>{doctor.clinic.publicPhone} <a href={`tel:${doctor.clinic.publicPhone.replace(/[^\d+]/g, '')}`}>Call</a></dd>
                </div>
              )}
            </>
          )}
        </dl>
        <NoticeList notices={availability.data?.notices ?? []} />
      </section>

      {today && today.sessions.some((s) => s.nowServing || s.waiting !== null) && (
        <section className="panel">
          <h2>Today's appointments</h2>
          <ul className="session-grid">
            {today.sessions.filter((s) => s.waiting !== null).map((s) => (
              <li key={s.name} className="session-tile">
                <strong>{s.name}</strong>
                <span>{s.state === 'Open' ? `Now serving ${s.nowServing ?? '—'}` : s.state === 'Paused' ? 'Paused' : 'Not started'}</span>
                <span className="hint">{s.waiting} waiting</span>
              </li>
            ))}
          </ul>
        </section>
      )}

      <section className="panel">
        <h2>Book an appointment</h2>
        {availability.loading && !availability.data && <Loading />}
        {availability.error ? <p className="alert alert-error">{errorMessage(availability.error)}</p> : null}
        {days.length === 0 && availability.data && <p>This doctor has not published clinic hours yet.</p>}
        <div className="day-list">
          {days.filter((d) => d.sessions.length > 0).map((day) => (
            <div key={day.date} className="day">
              <h3>{formatDay(day.date)}</h3>
              <ul className="session-grid">
                {day.sessions.map((s) => (
                  <li key={s.name} className={`session-tile${s.bookable ? '' : ' unavailable'}`}>
                    <strong>{s.name}</strong>
                    <span>{formatTime(s.startsAt)} – {formatTime(s.endsAt)}</span>
                    {s.bookable ? (
                      <>
                        <span className="hint">{s.remaining} of {s.capacity} places left</span>
                        <Link className="button primary small" to={`/doctors/${doctor.doctorCode}/book?date=${day.date}&session=${encodeURIComponent(s.name)}`}>
                          Book appointment
                        </Link>
                      </>
                    ) : (
                      <span className="hint">{s.reason ?? 'Not available'}</span>
                    )}
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>
        <p className="hint">Tokens are serial numbers, not fixed times. Waiting times shown after booking are approximate.</p>
      </section>
      <Link to="/" className="button">Back to search</Link>
    </article>
  )
}
