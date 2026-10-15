import { useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { api, ApiError, errorMessage } from '../api/client'
import type { Availability, LiveStatus, PersonDto } from '../api/queueTypes'
import type { DoctorCard } from '../api/types'
import { useApi } from '../api/useApi'
import { FormAlert } from '../components/Fields'
import { Loading } from '../components/Layout'
import { formatDay, formatTime, newIdempotencyKey } from '../format'
import { FamilyMemberFields } from '../components/FamilyMemberFields'
import { familyMemberBody, personLabel } from '../people'

export function BookPage() {
  const { code = '' } = useParams()
  const [params] = useSearchParams()
  const date = params.get('date') ?? ''
  const sessionName = params.get('session') ?? ''
  const navigate = useNavigate()

  const doctor = useApi<DoctorCard>(`/doctors/${encodeURIComponent(code)}`)
  const availability = useApi<Availability>(`/doctors/${encodeURIComponent(code)}/availability`)
  const people = useApi<PersonDto[]>('/me/people')

  const [selected, setSelected] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const [addErrors, setAddErrors] = useState<Record<string, string[]>>({})
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [idempotencyKey] = useState(newIdempotencyKey)

  if (doctor.loading || availability.loading || people.loading) return <Loading />
  if (!doctor.data || !availability.data || !people.data) {
    return <p className="alert alert-error" role="alert">{errorMessage(doctor.error ?? availability.error ?? people.error)}</p>
  }

  const session = availability.data.days.find((d) => d.date === date)?.sessions.find((s) => s.name === sessionName)
  const person = people.data.find((p) => p.patientId === selected) ?? null

  if (!session) {
    return (
      <section className="panel narrow">
        <h1>Session not available</h1>
        <p>This session is no longer listed. Choose another one.</p>
        <Link className="button" to={`/doctors/${code}`}>Back to {doctor.data.fullName}</Link>
      </section>
    )
  }

  async function addFamilyMember(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setError(null)
    try {
      const created = await api<PersonDto>('/me/family', { method: 'POST', body: familyMemberBody(new FormData(event.currentTarget)) })
      setAddErrors({})
      setAdding(false)
      people.reload()
      setSelected(created.patientId)
    } catch (e) {
      if (e instanceof ApiError) setAddErrors(e.fieldErrors)
      setError(errorMessage(e))
    }
  }

  async function book(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!person) {
      setError('Choose who this appointment is for.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      const result = await api<LiveStatus>('/appointments', {
        method: 'POST',
        headers: { 'Idempotency-Key': idempotencyKey },
        body: { doctorCode: code, date, sessionName, patientId: person.patientId },
      })
      navigate(`/appointments/${result.appointmentId}`, { replace: true, state: { justBooked: true } })
    } catch (e) {
      setError(errorMessage(e))
      setBusy(false)
    }
  }

  return (
    <section className="panel narrow">
      <title>Book appointment · Smart Doctor</title>
      <h1>Book an appointment</h1>
      <dl className="details compact">
        <div><dt>Doctor</dt><dd>{doctor.data.fullName}</dd></div>
        <div><dt>When</dt><dd>{formatDay(date)}, {session.name} ({formatTime(session.startsAt)} – {formatTime(session.endsAt)})</dd></div>
        <div><dt>Places left</dt><dd>{session.remaining}</dd></div>
      </dl>

      <FormAlert message={error} />

      {adding ? (
        <form method="post" onSubmit={addFamilyMember} className="subpanel">
          <h2>Add a family member</h2>
          <FamilyMemberFields errors={addErrors} />
          <div className="actions">
            <button type="submit" className="button primary">Add and select</button>
            <button type="button" className="button" onClick={() => setAdding(false)}>Cancel</button>
          </div>
        </form>
      ) : (
        <form method="post" onSubmit={book}>
          <fieldset className="field">
            <legend><h2 className="legend-heading">Who is this appointment for?</h2></legend>
            <div className="choice-list">
              {people.data.map((p) => (
                <label key={p.patientId} className="choice">
                  <input type="radio" name="patientId" value={p.patientId} checked={selected === p.patientId}
                    onChange={() => setSelected(p.patientId)} required />
                  <span>{personLabel(p)}</span>
                </label>
              ))}
            </div>
            <button type="button" className="link-button" onClick={() => setAdding(true)}>+ Add a family member</button>
          </fieldset>

          {person && (
            <p className="alert alert-info confirm-person" role="status">
              The doctor will see <strong>{person.fullName}</strong> as the patient for this visit.
            </p>
          )}

          <div className="actions">
            <button type="submit" className="button primary" disabled={busy || !person}>
              {busy ? 'Booking…' : person ? `Book token for ${person.fullName}` : 'Book token'}
            </button>
            <Link className="button" to={`/doctors/${code}`}>Back</Link>
          </div>
        </form>
      )}
    </section>
  )
}
