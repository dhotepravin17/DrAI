import { useState, type FormEvent } from 'react'
import { api, ApiError, errorMessage } from '../api/client'
import type { StaffResponse } from '../api/queueTypes'
import { useApi } from '../api/useApi'
import { FormAlert, TextField } from '../components/Fields'
import { Loading } from '../components/Layout'
import { formatDate } from '../format'
import { useClinicScope } from './clinicScopeState'

export function StaffPage() {
  const scope = useClinicScope()
  const staff = useApi<StaffResponse>(scope.current.role === 'Doctor' ? scope.path('/clinic/staff') : null)
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({})
  const [formKey, setFormKey] = useState(0)

  if (scope.current.role !== 'Doctor') return <p className="alert alert-info">Only the doctor manages staff.</p>

  async function invite(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const d = new FormData(event.currentTarget)
    setError(null)
    try {
      await api(scope.path('/clinic/staff'), { method: 'POST', body: { fullName: d.get('fullName'), mobileNumber: d.get('mobileNumber') } })
      setFieldErrors({})
      setFormKey((k) => k + 1)
      staff.reload()
    } catch (e) {
      if (e instanceof ApiError) setFieldErrors(e.fieldErrors)
      setError(errorMessage(e))
    }
  }

  async function remove(path: string) {
    try {
      await api(scope.path(path), { method: 'DELETE' })
      staff.reload()
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <section className="stack">
      <title>Staff · Smart Doctor</title>
      <h1>Clinic staff</h1>
      <p className="lead">Receptionists can add walk-ins, check patients in and manage appointments. They cannot change your schedule or staff.</p>
      <FormAlert message={error} />
      {staff.loading && !staff.data && <Loading />}
      {staff.data && (
        <div className="panel">
          <h2>Team</h2>
          {staff.data.members.length === 0 && staff.data.pending.length === 0 && <p className="hint">No staff yet.</p>}
          <ul className="card-list">
            {staff.data.members.map((m) => (
              <li key={m.assignmentId} className="subpanel person-card">
                <span><strong>{m.fullName}</strong> · {m.mobileMasked} · since {formatDate(m.grantedAt)}</span>
                <button type="button" className="button small danger" onClick={() => void remove(`/clinic/staff/${m.assignmentId}`)}>Remove access</button>
              </li>
            ))}
            {staff.data.pending.map((p) => (
              <li key={p.invitationId} className="subpanel person-card">
                <span>{p.fullName} · {p.mobileMasked} · <em>invitation pending</em></span>
                <button type="button" className="button small" onClick={() => void remove(`/clinic/staff/invitations/${p.invitationId}`)}>Withdraw</button>
              </li>
            ))}
          </ul>
        </div>
      )}
      <form key={formKey} className="panel narrow" onSubmit={invite}>
        <h2>Invite a receptionist</h2>
        <p className="hint">They sign in with this mobile number and accept the invitation from their Account page.</p>
        <TextField name="fullName" label="Name" autoComplete="off" maxLength={120} errors={fieldErrors.fullName} />
        <TextField name="mobileNumber" label="Mobile number" type="tel" inputMode="tel" autoComplete="off" errors={fieldErrors.mobileNumber} />
        <button type="submit" className="button primary">Send invitation</button>
      </form>
    </section>
  )
}
