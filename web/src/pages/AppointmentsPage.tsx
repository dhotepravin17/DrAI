import { useState } from 'react'
import { Link, useLocation, useParams } from 'react-router-dom'
import { api, ApiError, errorMessage } from '../api/client'
import { appointmentStatusLabels, type LiveStatus } from '../api/queueTypes'
import { useApi } from '../api/useApi'
import { usePolling, useQueueSignal } from '../api/useQueueSignal'
import { Loading } from '../components/Layout'
import { ModalForm } from '../components/Modal'
import { estimateText, formatDay, formatTime } from '../format'
import { NoticeList } from './DoctorProfilePage'

function headline(s: LiveStatus): { text: string; tone: string } {
  switch (s.status) {
    case 'Called':
    case 'InConsultation':
      return { text: "It's your turn. Please go to the doctor's cabin.", tone: 'success' }
    case 'Missed':
      return {
        text: `You missed your turn. You will be placed back in the queue after ${s.requeueAfterCompletions ?? 0} more patient(s).`,
        tone: 'warning',
      }
    case 'Completed':
      return { text: 'Consultation completed.', tone: 'success' }
    case 'Cancelled':
      return { text: 'This appointment was cancelled.', tone: 'danger' }
    case 'NoShow':
      return { text: 'Marked as not attended.', tone: 'danger' }
    default:
      if (s.patientsAhead === 0) return { text: 'You are next.', tone: 'success' }
      return { text: `${s.patientsAhead} patient${s.patientsAhead === 1 ? '' : 's'} ahead of you`, tone: 'info' }
  }
}

export function AppointmentsPage() {
  const { data, error, loading, reload } = useApi<LiveStatus[]>('/appointments')
  usePolling(reload, 30_000)

  return (
    <section className="stack">
      <title>My appointments · Smart Doctor Queue</title>
      <h1>My appointments</h1>
      {error ? <p className="alert alert-error" role="alert">{errorMessage(error)}</p> : null}
      {loading && !data && <Loading />}
      {data && data.length === 0 && (
        <div className="panel">
          <p>You have no upcoming appointments.</p>
          <Link to="/" className="button primary">Find a doctor</Link>
        </div>
      )}
      <ul className="card-list">
        {data?.map((s) => {
          const h = headline(s)
          return (
            <li key={s.appointmentId}>
              <Link to={`/appointments/${s.appointmentId}`} className="panel appointment-card">
                <span className="token-chip">{s.token}</span>
                <span className="appointment-main">
                  <strong>{s.patientName}</strong> with {s.doctorName}
                  <span className="meta">{formatDay(s.date)} · {s.sessionName}</span>
                  <span className={`tone-text tone-${h.tone}`}>{h.text}</span>
                </span>
                <span aria-hidden="true">→</span>
              </Link>
            </li>
          )
        })}
      </ul>
      <Link to="/visits">See past visits</Link>
    </section>
  )
}

export function AppointmentPage() {
  const { id = '' } = useParams()
  const location = useLocation()
  const justBooked = (location.state as { justBooked?: boolean } | null)?.justBooked ?? false
  const { data, error, loading, reload } = useApi<LiveStatus>(`/appointments/${id}`)
  const live = useQueueSignal(data && data.canCancel ? data.sessionId : null, reload)
  usePolling(reload, live ? 60_000 : 15_000, Boolean(data?.canCancel))
  const [cancelling, setCancelling] = useState(false)

  if (loading && !data) return <Loading />
  if (!data) {
    const missing = error instanceof ApiError && error.status === 404
    return <p className="alert alert-error" role="alert">{missing ? 'Appointment not found.' : errorMessage(error)}</p>
  }

  const h = headline(data)
  const estimate = estimateText(data.estimateMinMinutes, data.estimateMaxMinutes)
  const offline = Boolean(error)

  return (
    <section className="stack">
      <title>{`${data.token} · Smart Doctor Queue`}</title>
      {justBooked && <p className="alert alert-success" role="status">Booked. Keep this page open or turn on alerts to know when your turn is near.</p>}
      <div className="panel live-card" aria-live="polite">
        <p className="meta">{data.doctorName} · {formatDay(data.date)} · {data.sessionName}</p>
        <p className="token-display" aria-label={`Token ${data.token}`}>{data.token}</p>
        <p className="patient-line">for <strong>{data.patientName}</strong></p>
        <p className={`headline tone-text tone-${h.tone}`}>{h.text}</p>
        <dl className="details compact">
          {data.nowServing && <div><dt>Now serving</dt><dd className="code">{data.nowServing}</dd></div>}
          {estimate && <div><dt>Estimated wait</dt><dd>{estimate} <span className="hint">(approximate)</span></dd></div>}
          <div><dt>Status</dt><dd>{appointmentStatusLabels[data.status]}</dd></div>
          <div><dt>Session</dt><dd>{formatTime(data.sessionStartsAt)} – {formatTime(data.sessionEndsAt)}{data.sessionState === 'Paused' ? ' · paused' : ''}</dd></div>
        </dl>
        <p className="hint">
          {offline ? '⚠ Offline. ' : live ? '● Live. ' : ''}Last updated {formatTime(data.asOf)}.{' '}
          <button type="button" className="link-button" onClick={reload}>Refresh</button>
        </p>
      </div>

      <NoticeList notices={data.notices} />

      {data.followUpNote && (
        <div className="panel">
          <h2>Note from the doctor</h2>
          <p>{data.followUpNote}</p>
        </div>
      )}

      <div className="panel">
        <dl className="details compact">
          <div><dt>Clinic</dt><dd>{data.clinicName}, {data.clinicAddress}</dd></div>
          <div><dt>Reference</dt><dd className="code">{data.reference}</dd></div>
        </dl>
        <div className="actions">
          {data.doctorCode && <Link className="button" to={`/doctors/${data.doctorCode}`}>Doctor profile</Link>}
          {data.canCancel && <button type="button" className="button danger" onClick={() => setCancelling(true)}>Cancel appointment</button>}
        </div>
      </div>

      {cancelling && (
        <ModalForm title={`Cancel ${data.token}?`} submitLabel="Cancel appointment" danger onClose={() => setCancelling(false)}
          onSubmit={async () => {
            await api(`/appointments/${id}/cancel`, { method: 'POST' })
            reload()
          }}>
          {() => <p>{data.patientName}'s place in the queue will be released. Cancelled tokens are not reused.</p>}
        </ModalForm>
      )}
    </section>
  )
}
