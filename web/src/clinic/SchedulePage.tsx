import { useState, type FormEvent } from 'react'
import { api, ApiError, errorMessage } from '../api/client'
import { dayNames, type DayName, type QueueSettings, type ScheduleResponse, type SessionTemplate } from '../api/queueTypes'
import { useApi } from '../api/useApi'
import { FormAlert, TextField } from '../components/Fields'
import { Loading } from '../components/Layout'
import { addDays, formatDay, todayIst } from '../format'
import { useClinicScope } from './clinicScopeState'

const hhmm = (t: string) => t.slice(0, 5)

export function SchedulePage() {
  const scope = useClinicScope()
  const schedule = useApi<ScheduleResponse>(scope.path('/clinic/schedule'))
  const isDoctor = scope.current.role === 'Doctor'

  if (schedule.loading && !schedule.data) return <Loading />
  if (!schedule.data) return <p className="alert alert-error" role="alert">{errorMessage(schedule.error)}</p>

  return (
    <section className="stack">
      <title>Schedule · Valmedo</title>
      <h1>Schedule</h1>
      {!isDoctor && <p className="alert alert-info">Only the doctor can change the schedule. You can see it here.</p>}
      <WeeklySessions key={JSON.stringify(schedule.data.sessions)} initial={schedule.data.sessions} readOnly={!isDoctor} onSaved={schedule.reload} />
      <ExceptionsPanel data={schedule.data} readOnly={!isDoctor} onChanged={schedule.reload} />
      <SettingsPanel key={JSON.stringify(schedule.data.settings)} settings={schedule.data.settings} readOnly={!isDoctor} onSaved={schedule.reload} />
    </section>
  )
}

function WeeklySessions({ initial, readOnly, onSaved }: { initial: SessionTemplate[]; readOnly: boolean; onSaved: () => void }) {
  const scope = useClinicScope()
  const [rows, setRows] = useState<SessionTemplate[]>(() => initial.map((s) => ({ ...s, startTime: hhmm(s.startTime), endTime: hhmm(s.endTime) })))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)

  const update = (i: number, change: Partial<SessionTemplate>) => {
    setSaved(false)
    setRows((r) => r.map((row, j) => (j === i ? { ...row, ...change } : row)))
  }

  function copyToWeekdays(source: DayName) {
    const template = rows.filter((r) => r.dayOfWeek === source)
    const weekdays: DayName[] = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
    setRows((r) => [...r.filter((x) => !weekdays.includes(x.dayOfWeek) || x.dayOfWeek === source),
      ...weekdays.filter((d) => d !== source).flatMap((d) => template.map((t) => ({ ...t, dayOfWeek: d })))])
    setSaved(false)
  }

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api(scope.path('/clinic/schedule/sessions'), { method: 'PUT', body: rows })
      setSaved(true)
      onSaved()
    } catch (e) {
      setError(e instanceof ApiError && Object.keys(e.fieldErrors).length > 0 ? Object.values(e.fieldErrors).flat().join(' ') : errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  const sorted = rows.map((r, i) => ({ r, i })).sort((a, b) => dayNames.indexOf(a.r.dayOfWeek) - dayNames.indexOf(b.r.dayOfWeek) || a.r.startTime.localeCompare(b.r.startTime))

  return (
    <form className="panel" onSubmit={save}>
      <h2>Weekly clinic hours</h2>
      <p className="hint">
        Changes apply to dates that do not have appointments yet. For a date that already has bookings, add a date exception below so patients are told.
      </p>
      <FormAlert message={error} />
      {saved && <p className="alert alert-success" role="status">Weekly hours saved.</p>}
      {rows.length === 0 && <p>No sessions yet. Add your first session.</p>}
      {rows.length > 0 && (
        <div className="table-wrap">
          <table>
            <thead><tr><th scope="col">Day</th><th scope="col">Session</th><th scope="col">Starts</th><th scope="col">Ends</th><th scope="col">Online places</th><th scope="col"><span className="visually-hidden">Actions</span></th></tr></thead>
            <tbody>
              {sorted.map(({ r, i }) => (
                <tr key={i}>
                  <td>
                    <select aria-label="Day" value={r.dayOfWeek} disabled={readOnly} onChange={(e) => update(i, { dayOfWeek: e.target.value as DayName })}>
                      {dayNames.map((d) => <option key={d} value={d}>{d}</option>)}
                    </select>
                  </td>
                  <td><input aria-label="Session name" value={r.name} maxLength={30} disabled={readOnly} required onChange={(e) => update(i, { name: e.target.value })} /></td>
                  <td><input aria-label="Start time" type="time" value={r.startTime} disabled={readOnly} required onChange={(e) => update(i, { startTime: e.target.value })} /></td>
                  <td><input aria-label="End time" type="time" value={r.endTime} disabled={readOnly} required onChange={(e) => update(i, { endTime: e.target.value })} /></td>
                  <td><input aria-label="Capacity" type="number" min={1} max={500} value={r.capacity} disabled={readOnly} required onChange={(e) => update(i, { capacity: Number(e.target.value) })} /></td>
                  <td>{!readOnly && <button type="button" className="button small" onClick={() => setRows((x) => x.filter((_, j) => j !== i))}>Remove</button>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {!readOnly && (
        <div className="actions">
          <button type="button" className="button" onClick={() => setRows((r) => [...r, { dayOfWeek: 'Monday', name: 'Morning', startTime: '09:00', endTime: '13:00', capacity: 30 }])}>Add session</button>
          {rows.some((r) => r.dayOfWeek === 'Monday') && <button type="button" className="button" onClick={() => copyToWeekdays('Monday')}>Copy Monday to Tue–Sat</button>}
          <button type="submit" className="button primary" disabled={busy}>{busy ? 'Saving…' : 'Save weekly hours'}</button>
        </div>
      )}
    </form>
  )
}

function ExceptionsPanel({ data, readOnly, onChanged }: { data: ScheduleResponse; readOnly: boolean; onChanged: () => void }) {
  const scope = useClinicScope()
  const [kind, setKind] = useState<'Closed' | 'SpecialHours'>('Closed')
  const [impact, setImpact] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({})
  const [busy, setBusy] = useState(false)
  const [formKey, setFormKey] = useState(0)
  const sessionNames = [...new Set(data.sessions.map((s) => s.name))]

  function bodyFrom(form: HTMLFormElement) {
    const d = new FormData(form)
    const text = (n: string) => String(d.get(n) ?? '').trim() || null
    return {
      kind, startDate: text('startDate'), endDate: text('endDate'), sessionName: text('sessionName'),
      startTime: text('startTime'), endTime: text('endTime'), capacity: text('capacity') ? Number(text('capacity')) : null, reason: text('reason'),
    }
  }

  async function preview(form: HTMLFormElement) {
    const b = bodyFrom(form)
    if (!b.startDate) return
    try {
      const q = new URLSearchParams({ startDate: b.startDate })
      if (b.endDate) q.set('endDate', b.endDate)
      if (b.sessionName) q.set('sessionName', b.sessionName)
      const r = await api<{ sessions: number; activeAppointments: number }>(scope.path(`/clinic/schedule/impact?${q}`))
      setImpact(r.activeAppointments === 0 ? 'No booked patients are affected.'
        : kind === 'Closed' ? `${r.activeAppointments} booked patient(s) will be cancelled and notified.`
          : `${r.activeAppointments} booked patient(s) will be notified of the new hours.`)
    } catch {
      setImpact(null)
    }
  }

  async function add(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api(scope.path('/clinic/schedule/exceptions'), { method: 'POST', body: bodyFrom(event.currentTarget) })
      setFieldErrors({})
      setImpact(null)
      setFormKey((k) => k + 1)
      onChanged()
    } catch (e) {
      if (e instanceof ApiError) setFieldErrors(e.fieldErrors)
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  async function remove(id: number) {
    try {
      await api(scope.path(`/clinic/schedule/exceptions/${id}`), { method: 'DELETE' })
      onChanged()
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <div className="panel">
      <h2>Holidays and special hours</h2>
      {data.exceptions.length === 0 ? <p className="hint">No date exceptions.</p> : (
        <ul className="card-list">
          {data.exceptions.map((x) => (
            <li key={x.id} className="subpanel person-card">
              <span>
                <strong>{x.kind === 'Closed' ? 'Closed' : 'Special hours'}</strong>{' '}
                {formatDay(x.startDate)}{x.endDate !== x.startDate ? ` – ${formatDay(x.endDate)}` : ''}
                {x.sessionName ? ` · ${x.sessionName}` : ' · all sessions'}
                {x.startTime && x.endTime ? ` · ${hhmm(x.startTime)}–${hhmm(x.endTime)}` : ''}
                {x.reason ? <span className="hint"> · {x.reason}</span> : null}
                {x.announcementId ? <span className="hint"> (from an announcement)</span> : null}
              </span>
              {!readOnly && !x.announcementId && <button type="button" className="button small" onClick={() => void remove(x.id)}>Remove</button>}
            </li>
          ))}
        </ul>
      )}

      {!readOnly && (
        <form key={formKey} className="subpanel" onSubmit={add} onChange={(e) => void preview(e.currentTarget)}>
          <h3>Add a date exception</h3>
          <FormAlert message={error} />
          <fieldset className="field">
            <legend>Type</legend>
            <div className="radio-row">
              <label className="radio"><input type="radio" name="kind" checked={kind === 'Closed'} onChange={() => setKind('Closed')} /> Closed</label>
              <label className="radio"><input type="radio" name="kind" checked={kind === 'SpecialHours'} onChange={() => setKind('SpecialHours')} /> Special hours</label>
            </div>
          </fieldset>
          <div className="form-row">
            <TextField name="startDate" label="From" type="date" min={todayIst()} defaultValue={addDays(todayIst(), 1)} errors={fieldErrors.startDate} />
            <TextField name="endDate" label="To" type="date" optional min={todayIst()} errors={fieldErrors.endDate} />
          </div>
          <div className="field">
            <label htmlFor="exception-session">Session{kind === 'Closed' && <span className="optional"> (leave empty for the whole day)</span>}</label>
            <select id="exception-session" name="sessionName" required={kind === 'SpecialHours'}>
              <option value="">{kind === 'Closed' ? 'All sessions' : 'Choose…'}</option>
              {sessionNames.map((n) => <option key={n} value={n}>{n}</option>)}
            </select>
          </div>
          {kind === 'SpecialHours' && (
            <div className="form-row">
              <TextField name="startTime" label="Starts" type="time" errors={fieldErrors.startTime} />
              <TextField name="endTime" label="Ends" type="time" errors={fieldErrors.endTime} />
              <TextField name="capacity" label="Online places" type="number" min={1} max={500} defaultValue="30" errors={fieldErrors.capacity} />
            </div>
          )}
          <TextField name="reason" label="Reason shown to patients" optional maxLength={280} errors={fieldErrors.reason} />
          {impact && <p className="alert alert-warning" role="status">{impact}</p>}
          <button type="submit" className="button primary" disabled={busy}>{busy ? 'Saving…' : 'Add exception'}</button>
        </form>
      )}
    </div>
  )
}

function SettingsPanel({ settings, readOnly, onSaved }: { settings: QueueSettings; readOnly: boolean; onSaved: () => void }) {
  const scope = useClinicScope()
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({})
  const [saved, setSaved] = useState(false)

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const d = new FormData(event.currentTarget)
    const n = (k: string) => Number(d.get(k))
    setError(null)
    try {
      await api(scope.path('/clinic/settings'), {
        method: 'PUT',
        body: {
          bookingWindowDays: n('bookingWindowDays'), avgConsultMinutes: n('avgConsultMinutes'), gracePeriodMinutes: n('gracePeriodMinutes'),
          requeueAfterCompletions: n('requeueAfterCompletions'), turnApproachingThreshold: n('turnApproachingThreshold'),
          secondMissRule: d.get('secondMissRule'),
        },
      })
      setSaved(true)
      setFieldErrors({})
      onSaved()
    } catch (e) {
      if (e instanceof ApiError) setFieldErrors(e.fieldErrors)
      setError(errorMessage(e))
    }
  }

  return (
    <form className="panel" onSubmit={save}>
      <h2>Appointment rules</h2>
      <FormAlert message={error} />
      {saved && <p className="alert alert-success" role="status">Appointment rules saved.</p>}
      <fieldset disabled={readOnly}>
        <div className="form-row">
          <TextField name="bookingWindowDays" label="Booking opens (days ahead)" type="number" min={0} max={30} defaultValue={settings.bookingWindowDays} errors={fieldErrors.bookingWindowDays} />
          <TextField name="avgConsultMinutes" label="Typical consultation (min)" type="number" min={1} max={60} defaultValue={settings.avgConsultMinutes}
            hint="Used for estimates until there are 10 recent consultations" errors={fieldErrors.avgConsultMinutes} />
        </div>
        <div className="form-row">
          <TextField name="gracePeriodMinutes" label="Wait before marking missed (min)" type="number" min={0} max={15} defaultValue={settings.gracePeriodMinutes} errors={fieldErrors.gracePeriodMinutes} />
          <TextField name="requeueAfterCompletions" label="Return a missed patient to the list after" type="number" min={1} max={10} defaultValue={settings.requeueAfterCompletions}
            hint="Consultations by other patients" errors={fieldErrors.requeueAfterCompletions} />
          <TextField name="turnApproachingThreshold" label="Alert when this many are ahead" type="number" min={1} max={10} defaultValue={settings.turnApproachingThreshold} errors={fieldErrors.turnApproachingThreshold} />
        </div>
        <div className="field">
          <label htmlFor="secondMissRule">If a patient misses their turn twice</label>
          <select id="secondMissRule" name="secondMissRule" defaultValue={settings.secondMissRule}>
            <option value="NoShow">Mark as not attended</option>
            <option value="RequeueAgain">Return them to the list again</option>
          </select>
        </div>
        {!readOnly && <button type="submit" className="button primary">Save appointment rules</button>}
      </fieldset>
    </form>
  )
}
