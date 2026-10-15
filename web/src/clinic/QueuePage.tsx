import { useState } from 'react'
import { Link } from 'react-router-dom'
import { api, errorMessage } from '../api/client'
import {
  appointmentStatusLabels, emergencyLabels, sourceLabels, type EmergencyReason, type PatientMatch, type QueueEntry,
  type QueueView, type SessionSummary,
} from '../api/queueTypes'
import { useApi } from '../api/useApi'
import { usePolling, useQueueSignal } from '../api/useQueueSignal'
import { SelectField, TextAreaField, TextField } from '../components/Fields'
import { Loading } from '../components/Layout'
import { ModalForm } from '../components/Modal'
import { addDays, formatDay, formatTime, todayIst } from '../format'
import { ClinicSetupChecklist } from './ClinicSetup'
import { useClinicScope } from './clinicScopeState'

const emergencyOptions = (Object.keys(emergencyLabels) as EmergencyReason[]).map((r) => ({ value: r, label: emergencyLabels[r] }))

type Dialog =
  | { kind: 'complete'; entry: QueueEntry }
  | { kind: 'prioritize'; entry: QueueEntry }
  | { kind: 'cancel'; entry: QueueEntry }
  | { kind: 'close' }
  | { kind: 'walk-in' }

function Name({ entry }: { entry: QueueEntry }) {
  return (
    <>
      <strong>{entry.patientName}</strong>
      {entry.patientAge !== null && <span className="hint"> · {entry.patientAge} y</span>}
      {entry.priority === 'Emergency' && (
        <span className="badge badge-danger">Emergency{entry.emergencyReason ? `: ${emergencyLabels[entry.emergencyReason]}` : ''}</span>
      )}
      {entry.bookedByName && <div className="hint">Booked by {entry.bookedByName}</div>}
    </>
  )
}

export function QueuePage() {
  const scope = useClinicScope()
  const [date, setDate] = useState(todayIst)
  const sessions = useApi<SessionSummary[]>(scope.path(`/clinic/sessions?date=${date}`))
  const [chosen, setChosen] = useState<string | null>(null)
  const sessionId =
    sessions.data?.find((s) => s.sessionId === chosen)?.sessionId ??
    sessions.data?.find((s) => s.state === 'Open' || s.state === 'Paused')?.sessionId ??
    sessions.data?.find((s) => s.state === 'Scheduled')?.sessionId ??
    sessions.data?.[0]?.sessionId ??
    null

  const view = useApi<QueueView>(sessionId ? scope.path(`/clinic/sessions/${sessionId}`) : null)
  const live = useQueueSignal(sessionId, view.reload)
  usePolling(view.reload, live ? 60_000 : 15_000, Boolean(sessionId))
  const [now, setNow] = useState(() => Date.now())
  usePolling(() => setNow(Date.now()), 5_000)

  const [dialog, setDialog] = useState<Dialog | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function run(path: string, body?: unknown) {
    setBusy(true)
    setError(null)
    try {
      await api(scope.path(path), { method: 'POST', body })
      view.reload()
      sessions.reload()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  const v = view.data && view.data.session.sessionId === sessionId ? view.data : null
  const finished = v ? v.session.state === 'Closed' || v.session.state === 'Cancelled' : true
  const served = v?.nowServing ?? null
  const missAllowed = served?.missAllowedAt ? new Date(served.missAllowedAt).getTime() <= now : false

  return (
    <section className="stack">
      <title>Appointments · Smart Doctor</title>
      <div className="page-head">
        <h1>Appointments</h1>
        <div className="inline-field">
          <button type="button" className="button small" onClick={() => setDate(addDays(date, -1))} aria-label="Previous day">‹</button>
          <input type="date" aria-label="Date" value={date} onChange={(e) => e.target.value && setDate(e.target.value)} />
          <button type="button" className="button small" onClick={() => setDate(addDays(date, 1))} aria-label="Next day">›</button>
          {date !== todayIst() && <button type="button" className="button small" onClick={() => setDate(todayIst())}>Today</button>}
        </div>
      </div>

      {sessions.loading && !sessions.data && <Loading />}
      <ClinicSetupChecklist />
      {sessions.data && sessions.data.length === 0 && (
        <p className="panel">
          No sessions on {formatDay(date)}.{' '}
          {scope.current.role === 'Doctor'
            ? <>Set your weekly hours, or open special hours for this date, on the <Link to="/clinic/schedule">Schedule</Link> page.</>
            : 'The doctor sets clinic hours on the Schedule page.'}
        </p>
      )}
      {sessions.data && sessions.data.length > 0 && (
        <div className="tabs" role="tablist" aria-label="Sessions">
          {sessions.data.map((s) => (
            <button key={s.sessionId} type="button" role="tab" className="tab" aria-selected={s.sessionId === sessionId}
              aria-current={s.sessionId === sessionId ? 'page' : undefined} onClick={() => setChosen(s.sessionId)}>
              {s.name} · {formatTime(s.startsAt)} <span className="hint">({s.waiting} waiting)</span>
            </button>
          ))}
        </div>
      )}

      {error && <p className="alert alert-error" role="alert">{error}</p>}
      {sessionId && !v && <Loading />}

      {v && (
        <>
          <div className="panel queue-summary">
            <div className="summary-stats">
              <span className={`status status-${v.session.state}`}>{v.session.state}</span>
              <span><strong>{v.session.booked}</strong> / {v.session.capacity} booked</span>
              <span><strong>{v.session.waiting}</strong> waiting</span>
              <span><strong>{v.session.completed}</strong> seen</span>
              <span><strong>{v.session.missed}</strong> missed</span>
              <span className="hint">~{v.avgConsultMinutes} min each{v.session.delayMinutes ? ` · ${v.session.delayMinutes} min delay` : ''}</span>
              <span className="hint">{live ? '● Live' : 'Refreshing every 15 s'}</span>
            </div>
            <div className="actions">
              {(v.session.state === 'Scheduled' || v.session.state === 'Paused') && (
                <button type="button" className="button" disabled={busy}
                  onClick={() => void run(`/clinic/sessions/${sessionId}/${v.session.state === 'Paused' ? 'resume' : 'open'}`)}>
                  {v.session.state === 'Paused' ? 'Resume appointments' : 'Start appointments'}
                </button>
              )}
              {v.session.state === 'Open' && (
                <button type="button" className="button" disabled={busy} onClick={() => void run(`/clinic/sessions/${sessionId}/pause`)}>Pause appointments</button>
              )}
              {!finished && <button type="button" className="button" disabled={busy} onClick={() => setDialog({ kind: 'walk-in' })}>+ Add walk-in</button>}
              {!finished && <button type="button" className="button danger" disabled={busy} onClick={() => setDialog({ kind: 'close' })}>Close session</button>}
            </div>
          </div>

          <div className="queue-grid">
            <div className="panel now-serving">
              <h2>Now serving</h2>
              {served ? (
                <>
                  <p className="token-display">{served.token}</p>
                  <p><Name entry={served} /></p>
                  <p className="hint">{appointmentStatusLabels[served.status]} since {formatTime(served.calledAt)}</p>
                  <div className="actions">
                    {served.status === 'Called' && (
                      <button type="button" className="button" disabled={busy} onClick={() => void run(`/clinic/appointments/${served.appointmentId}/start`)}>Start consultation</button>
                    )}
                    <button type="button" className="button primary" disabled={busy} onClick={() => setDialog({ kind: 'complete', entry: served })}>Complete</button>
                    {served.status === 'Called' && (
                      <button type="button" className="button" disabled={busy || !missAllowed}
                        title={missAllowed ? undefined : `Available at ${formatTime(served.missAllowedAt)}`}
                        onClick={() => void run(`/clinic/appointments/${served.appointmentId}/miss`)}>
                        {missAllowed ? 'Mark missed' : `Missed (after ${formatTime(served.missAllowedAt)})`}
                      </button>
                    )}
                  </div>
                </>
              ) : (
                <p className="hint">Nobody is with the doctor.</p>
              )}
              <button type="button" className="button primary call-next" disabled={busy || finished || Boolean(served) || v.waiting.length === 0}
                onClick={() => void run(`/clinic/sessions/${sessionId}/call-next`)}>
                {v.waiting.length === 0 ? 'No one waiting' : `Call next: ${v.waiting[0].token}`}
              </button>
            </div>

            <div className="panel">
              <h2>Waiting ({v.waiting.length})</h2>
              {v.waiting.length === 0 ? <p className="hint">No patients are waiting.</p> : (
                <ol className="queue-list">
                  {v.waiting.map((e) => (
                    <li key={e.appointmentId} className={e.priority === 'Emergency' ? 'emergency' : ''}>
                      <span className="token-chip">{e.token}</span>
                      <span className="queue-person">
                        <Name entry={e} />
                        <span className="hint">
                          {sourceLabels[e.source]} · {e.checkedInAt ? `arrived ${formatTime(e.checkedInAt)}` : `booked ${formatTime(e.bookedAt)}`}
                          {e.status === 'Requeued' ? ' · back after missed turn' : ''}
                        </span>
                      </span>
                      <span className="actions">
                        {!served && !finished && <button type="button" className="button small" disabled={busy} onClick={() => void run(`/clinic/appointments/${e.appointmentId}/call`)}>Call</button>}
                        {e.status === 'Booked' && <button type="button" className="button small" disabled={busy} onClick={() => void run(`/clinic/appointments/${e.appointmentId}/check-in`)}>Arrived</button>}
                        {e.priority !== 'Emergency' && <button type="button" className="button small" disabled={busy} onClick={() => setDialog({ kind: 'prioritize', entry: e })}>Emergency</button>}
                        <button type="button" className="button small" disabled={busy} onClick={() => void run(`/clinic/appointments/${e.appointmentId}/defer`)}>Move to end</button>
                        <button type="button" className="button small" disabled={busy} onClick={() => setDialog({ kind: 'cancel', entry: e })}>Cancel</button>
                      </span>
                    </li>
                  ))}
                </ol>
              )}
            </div>
          </div>

          {v.missed.length > 0 && (
            <div className="panel">
              <h2>Missed turn ({v.missed.length})</h2>
              <ul className="queue-list">
                {v.missed.map((e) => (
                  <li key={e.appointmentId}>
                    <span className="token-chip">{e.token}</span>
                    <span className="queue-person"><Name entry={e} />
                      <span className="hint">Back in the appointment list after {e.requeueAfterCompletions} more consultation{e.requeueAfterCompletions === 1 ? '' : 's'}</span>
                    </span>
                    <span className="actions">
                      <button type="button" className="button small" disabled={busy} onClick={() => void run(`/clinic/appointments/${e.appointmentId}/requeue`)}>Patient is here — add back now</button>
                      <button type="button" className="button small" disabled={busy} onClick={() => setDialog({ kind: 'cancel', entry: e })}>Cancel</button>
                    </span>
                  </li>
                ))}
              </ul>
            </div>
          )}

          {v.done.length > 0 && (
            <details className="panel">
              <summary><h2 className="inline-heading">Done ({v.done.length})</h2></summary>
              <ul className="queue-list">
                {v.done.map((e) => (
                  <li key={e.appointmentId}>
                    <span className="token-chip muted">{e.token}</span>
                    <span className="queue-person"><Name entry={e} /></span>
                    <span className="hint">{appointmentStatusLabels[e.status]}{e.completedAt ? ` · ${formatTime(e.completedAt)}` : ''}</span>
                  </li>
                ))}
              </ul>
            </details>
          )}
        </>
      )}

      {dialog?.kind === 'complete' && (
        <ModalForm title={`Complete ${dialog.entry.token}`} submitLabel="Complete consultation" onClose={() => setDialog(null)}
          onSubmit={async (data) => {
            await api(scope.path(`/clinic/appointments/${dialog.entry.appointmentId}/complete`), {
              method: 'POST', body: { followUpNote: String(data.get('followUpNote') ?? '') },
            })
            view.reload()
          }}>
          {(errors) => (
            <TextAreaField name="followUpNote" label="Follow-up note for the patient" optional maxLength={500}
              hint="Shown in the patient's visit history, for example 'Review in one week'. Do not enter a diagnosis." errors={errors.followUpNote} />
          )}
        </ModalForm>
      )}

      {dialog?.kind === 'prioritize' && (
        <ModalForm title={`Prioritize ${dialog.entry.token} — ${dialog.entry.patientName}`} submitLabel="Move to the front" onClose={() => setDialog(null)}
          onSubmit={async (data) => {
            await api(scope.path(`/clinic/appointments/${dialog.entry.appointmentId}/prioritize`), {
              method: 'POST', body: { reason: data.get('reason'), note: data.get('note') },
            })
            view.reload()
          }}>
          {(errors) => (
            <>
              <SelectField name="reason" label="Reason" options={emergencyOptions} errors={errors.reason} />
              <TextField name="note" label="Note for the clinic team" optional maxLength={200} errors={errors.note}
                hint="Visible to clinic staff only. Never shown to other patients or included in alerts." />
            </>
          )}
        </ModalForm>
      )}

      {dialog?.kind === 'cancel' && (
        <ModalForm title={`Cancel ${dialog.entry.token} — ${dialog.entry.patientName}?`} submitLabel="Cancel appointment" danger onClose={() => setDialog(null)}
          onSubmit={async (data) => {
            await api(scope.path(`/clinic/appointments/${dialog.entry.appointmentId}/cancel`), { method: 'POST', body: { note: data.get('note') } })
            view.reload()
          }}>
          {(errors) => <TextField name="note" label="Reason" optional maxLength={200} errors={errors.note} />}
        </ModalForm>
      )}

      {dialog?.kind === 'close' && v && (
        <ModalForm title={`Close the ${v.session.name} session?`} submitLabel="Close session" danger onClose={() => setDialog(null)}
          onSubmit={async () => {
            await api(scope.path(`/clinic/sessions/${sessionId}/close`), { method: 'POST' })
            view.reload()
            sessions.reload()
          }}>
          {() => (
            <p>
              {v.waiting.length > 0
                ? `${v.waiting.length} waiting patient(s) will be cancelled and notified. `
                : 'Nobody is waiting. '}
              Patients who missed their turn will be marked as not attended.
            </p>
          )}
        </ModalForm>
      )}

      {dialog?.kind === 'walk-in' && sessionId && (
        <ModalForm title="Add a walk-in" submitLabel="Add appointment" onClose={() => setDialog(null)}
          onSubmit={async (data) => {
            const existing = String(data.get('patientId') ?? '')
            const emergency = data.get('emergency') === 'on'
            await api(scope.path(`/clinic/sessions/${sessionId}/walk-ins`), {
              method: 'POST',
              body: {
                patientId: existing && existing !== 'new' ? existing : null,
                fullName: data.get('fullName'),
                mobileNumber: data.get('mobileNumber'),
                ageYears: data.get('ageYears') ? Number(data.get('ageYears')) : null,
                emergency: emergency ? { reason: data.get('emergencyReason'), note: data.get('emergencyNote') } : null,
              },
            })
            view.reload()
            sessions.reload()
          }}>
          {(errors) => <WalkInFields errors={errors} />}
        </ModalForm>
      )}
    </section>
  )
}

function WalkInFields({ errors }: { errors: Record<string, string[]> }) {
  const scope = useClinicScope()
  const [mobile, setMobile] = useState('')
  const [matches, setMatches] = useState<PatientMatch[] | null>(null)
  const [choice, setChoice] = useState('new')
  const [lookupError, setLookupError] = useState<string | null>(null)
  const [emergency, setEmergency] = useState(false)

  async function lookup() {
    setLookupError(null)
    try {
      const found = await api<PatientMatch[]>(scope.path(`/clinic/patients/lookup?mobile=${encodeURIComponent(mobile)}`))
      setMatches(found)
      setChoice(found[0]?.patientId ?? 'new')
    } catch (e) {
      setLookupError(errorMessage(e))
    }
  }

  return (
    <>
      <div className="field">
        <label htmlFor="walkin-mobile">Mobile number <span className="optional">(optional)</span></label>
        <p className="hint" id="walkin-mobile-hint">Find a registered patient or family member by their number.</p>
        <div className="inline-search">
          <input id="walkin-mobile" name="mobileNumber" type="tel" inputMode="tel" autoComplete="off" value={mobile}
            aria-describedby="walkin-mobile-hint" onChange={(e) => setMobile(e.target.value)} />
          <button type="button" className="button" onClick={() => void lookup()} disabled={mobile.trim().length < 10}>Find</button>
        </div>
        {lookupError && <p className="field-error" role="alert">{lookupError}</p>}
        {errors.mobileNumber && <p className="field-error" role="alert">{errors.mobileNumber.join(' ')}</p>}
      </div>

      {matches && (
        <fieldset className="field">
          <legend>Who is visiting?</legend>
          <div className="choice-list">
            {matches.map((m) => (
              <label key={m.patientId} className="choice">
                <input type="radio" name="patientId" value={m.patientId} checked={choice === m.patientId} onChange={() => setChoice(m.patientId)} />
                <span>{m.fullName}{m.ageYears !== null ? `, ${m.ageYears} y` : ''}</span>
              </label>
            ))}
            <label className="choice">
              <input type="radio" name="patientId" value="new" checked={choice === 'new'} onChange={() => setChoice('new')} />
              <span>Someone else (new patient)</span>
            </label>
          </div>
        </fieldset>
      )}

      {choice === 'new' && (
        <>
          <TextField name="fullName" label="Patient's full name" autoComplete="off" maxLength={120} errors={errors.fullName} />
          <TextField name="ageYears" label="Age in years" type="number" inputMode="numeric" min={0} max={120} optional errors={errors.ageYears} />
        </>
      )}

      <label className="checkbox">
        <input type="checkbox" name="emergency" checked={emergency} onChange={(e) => setEmergency(e.target.checked)} />
        <span>Emergency — see before others</span>
      </label>
      {emergency && (
        <>
          <SelectField name="emergencyReason" label="Reason" options={emergencyOptions} errors={errors['emergency.reason']} />
          <TextField name="emergencyNote" label="Note for the clinic team" optional maxLength={200} />
        </>
      )}
    </>
  )
}
