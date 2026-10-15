import { useState, type FormEvent } from 'react'
import { api, ApiError, errorMessage } from '../api/client'
import type { StaffInvitationReceived } from '../api/queueTypes'
import { useApi } from '../api/useApi'
import { useAuth } from '../auth/authState'
import { FormAlert, TextField } from '../components/Fields'
import { clearFieldErrorOnInput, formatDate, todayIst } from '../format'

export function AccountPage() {
  const { me, refreshMe, signOut } = useAuth()
  const invitations = useApi<StaffInvitationReceived[]>('/me/staff-invitations')
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({})
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  if (!me) return null

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const data = new FormData(event.currentTarget)
    const text = (name: string) => String(data.get(name) ?? '').trim() || null
    setBusy(true)
    setError(null)
    setMessage(null)
    try {
      await api('/me/profile', {
        method: 'PUT',
        body: { fullName: text('fullName'), dateOfBirth: text('dateOfBirth'), gender: text('gender'), city: text('city'), email: text('email') },
      })
      setFieldErrors({})
      setMessage('Profile saved.')
      await refreshMe()
    } catch (e) {
      if (e instanceof ApiError) setFieldErrors(e.fieldErrors)
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  async function respond(id: string, action: 'accept' | 'decline') {
    try {
      await api(`/me/staff-invitations/${id}/${action}`, { method: 'POST' })
      setMessage(action === 'accept' ? 'You can now manage this clinic’s appointments from the Clinic menu.' : 'Invitation declined.')
      invitations.reload()
      await refreshMe()
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  async function exportData() {
    try {
      const data = await api<unknown>('/me/export')
      const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }))
      const link = document.createElement('a')
      link.href = url
      link.download = 'valmedo-data.json'
      link.click()
      URL.revokeObjectURL(url)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <section className="stack">
      <title>Account · Valmedo</title>
      <h1>Account</h1>
      {message && <p className="alert alert-success" role="status">{message}</p>}
      <FormAlert message={error} />

      {invitations.data && invitations.data.length > 0 && (
        <div className="panel">
          <h2>Clinic invitations</h2>
          <ul className="card-list">
            {invitations.data.map((i) => (
              <li key={i.invitationId} className="subpanel person-card">
                <span><strong>{i.doctorName}</strong> ({i.clinicName}) invited you to manage their appointments · {formatDate(i.createdAt)}</span>
                <div className="actions">
                  <button type="button" className="button primary small" onClick={() => void respond(i.invitationId, 'accept')}>Accept</button>
                  <button type="button" className="button small" onClick={() => void respond(i.invitationId, 'decline')}>Decline</button>
                </div>
              </li>
            ))}
          </ul>
        </div>
      )}

      <form method="post" className="panel" onSubmit={save} onInput={clearFieldErrorOnInput(setFieldErrors)}>
        <h2>Your details</h2>
        <p className="hint">Mobile number {me.mobileNumberMasked}</p>
        <TextField name="fullName" label="Full name" autoComplete="name" defaultValue={me.fullName} maxLength={120} errors={fieldErrors.fullName} />
        <TextField name="dateOfBirth" label="Date of birth" type="date" optional max={todayIst()} defaultValue={me.patient?.dateOfBirth ?? ''}
          autoComplete="bday" errors={fieldErrors.dateOfBirth} />
        <fieldset className="field">
          <legend>Gender <span className="optional">(optional)</span></legend>
          <div className="radio-row">
            {([['Female', 'Female'], ['Male', 'Male'], ['Other', 'Other'], ['PreferNotToSay', 'Prefer not to say']] as const).map(([value, label]) => (
              <label key={value} className="radio">
                <input type="radio" name="gender" value={value} defaultChecked={me.patient?.gender === value} /> {label}
              </label>
            ))}
          </div>
        </fieldset>
        <TextField name="city" label="City" optional autoComplete="address-level2" defaultValue={me.patient?.city ?? ''} errors={fieldErrors.city} />
        <TextField name="email" label="Email" type="email" optional autoComplete="email" spellCheck={false} defaultValue={me.email ?? ''} errors={fieldErrors.email} />
        <button type="submit" className="button primary" disabled={busy}>{busy ? 'Saving…' : 'Save details'}</button>
      </form>

      <div className="panel">
        <h2>Your data</h2>
        <p>Download a copy of the personal data held for your account, including family profiles and appointments.</p>
        <div className="actions">
          <button type="button" className="button" onClick={() => void exportData()}>Download my data</button>
          <button type="button" className="button" onClick={signOut}>Sign out</button>
        </div>
      </div>
    </section>
  )
}
