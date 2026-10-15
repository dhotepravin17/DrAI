import { useState, type FormEvent } from 'react'
import { api, ApiError, errorMessage } from '../api/client'
import { announcementLabels, type AnnouncementDto, type AnnouncementType } from '../api/queueTypes'
import { useApi } from '../api/useApi'
import { FormAlert, SelectField, TextAreaField, TextField } from '../components/Fields'
import { Loading } from '../components/Layout'
import { formatDate, todayIst } from '../format'
import { useClinicScope } from './clinicScopeState'

const types = Object.keys(announcementLabels) as AnnouncementType[]
const closing: AnnouncementType[] = ['OutOfClinic', 'Leave', 'Holiday', 'ClinicClosure']

/** datetime-local values are clinic (IST) times. */
const istInstant = (value: string | null) => (value ? `${value}:00+05:30` : null)

export function AnnouncementsPage() {
  const scope = useClinicScope()
  const list = useApi<AnnouncementDto[]>(scope.path('/clinic/announcements'))
  const [type, setType] = useState<AnnouncementType>('Holiday')
  const [length, setLength] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({})
  const [message, setMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [formKey, setFormKey] = useState(0)

  async function publish(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const d = new FormData(event.currentTarget)
    const text = (k: string) => String(d.get(k) ?? '').trim() || null
    setBusy(true)
    setError(null)
    try {
      await api(scope.path('/clinic/announcements'), {
        method: 'POST',
        body: {
          type, message: text('message'), startsAt: istInstant(text('startsAt')), endsAt: istInstant(text('endsAt')),
          delayMinutes: text('delayMinutes') ? Number(text('delayMinutes')) : null,
        },
      })
      setMessage(closing.includes(type) ? 'Published. Bookings in this period were cancelled and patients notified.' : 'Published.')
      setFieldErrors({})
      setFormKey((k) => k + 1)
      setLength(0)
      list.reload()
    } catch (e) {
      if (e instanceof ApiError) setFieldErrors(e.fieldErrors)
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  async function remove(id: string) {
    try {
      await api(scope.path(`/clinic/announcements/${id}`), { method: 'DELETE' })
      list.reload()
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <section className="stack">
      <title>Notices and leave · Smart Doctor</title>
      <h1>Notices and leave</h1>
      <p className="lead">
        Tell patients when you are out of the clinic, on leave, closed for a holiday, opening late or working changed hours.
        The notice appears as a banner on your profile and on patients' appointments. Closures cancel and notify affected bookings.
      </p>
      {message && <p className="alert alert-success" role="status">{message}</p>}

      <form key={formKey} className="panel" onSubmit={publish}>
        <h2>Post a notice</h2>
        <FormAlert message={error} />
        <SelectField name="type" label="Type" value={type} onChange={(e) => setType(e.target.value as AnnouncementType)}
          options={types.map((t) => ({ value: t, label: announcementLabels[t] }))} errors={fieldErrors.type} />
        {closing.includes(type) && (
          <p className="alert alert-warning">The clinic will be closed for these dates. Existing bookings are cancelled and patients are notified.</p>
        )}
        <TextAreaField name="message" label="Message for patients" maxLength={280} onInput={(e) => setLength(e.currentTarget.value.length)}
          hint={`${length}/280 characters. Do not include patient details.`} errors={fieldErrors.message} />
        <div className="form-row">
          <TextField name="startsAt" label="From" type="datetime-local" defaultValue={`${todayIst()}T08:00`} errors={fieldErrors.startsAt} />
          <TextField name="endsAt" label="Until" type="datetime-local" optional={!closing.includes(type)} errors={fieldErrors.endsAt} />
          {type === 'DelayedOpening' && (
            <TextField name="delayMinutes" label="Delay (minutes)" type="number" min={5} max={240} defaultValue="30" errors={fieldErrors.delayMinutes} />
          )}
        </div>
        <p className="hint">Times are Indian Standard Time.</p>
        <button type="submit" className="button primary" disabled={busy}>{busy ? 'Publishing…' : 'Publish'}</button>
      </form>

      <div className="panel">
        <h2>Recent announcements</h2>
        {list.loading && !list.data && <Loading />}
        {list.data?.length === 0 && <p className="hint">None.</p>}
        <ul className="card-list">
          {list.data?.map((a) => (
            <li key={a.id} className="subpanel person-card">
              <span>
                <strong>{announcementLabels[a.type]}</strong>{a.isCurrent ? '' : ' (ended)'}: {a.message}
                <span className="hint"> · {formatDate(a.startsAt)}{a.endsAt ? ` – ${formatDate(a.endsAt)}` : ''}{a.delayMinutes ? ` · ${a.delayMinutes} min` : ''}</span>
              </span>
              {a.isCurrent && <button type="button" className="button small" onClick={() => void remove(a.id)}>Remove</button>}
            </li>
          ))}
        </ul>
      </div>
    </section>
  )
}
