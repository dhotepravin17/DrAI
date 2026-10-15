import { useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api, ApiError, errorMessage } from '../../api/client'
import {
  planLabels, rejectionLabels, statusLabels, type AdminDoctorDetail, type DoctorPlan, type RejectionReason,
} from '../../api/types'
import { FormAlert, SelectField, TextAreaField, TextField } from '../../components/Fields'
import { useApi } from '../../api/useApi'
import { Loading } from '../../components/Layout'
import { useModalDialog } from '../../components/useModalDialog'
import { formatDate } from '../../format'

type Action = 'verify' | 'reject' | 'suspend' | 'reinstate' | 'deactivate'

const actionCopy: Record<Action, { title: string; button: string; noteLabel: string; noteRequired: boolean; danger: boolean }> = {
  verify: { title: 'Verify this doctor', button: 'Verify and issue Doctor ID', noteLabel: 'Internal note', noteRequired: false, danger: false },
  reject: { title: 'Reject this registration', button: 'Reject registration', noteLabel: 'Note to the doctor', noteRequired: false, danger: true },
  suspend: { title: 'Suspend this doctor', button: 'Suspend doctor', noteLabel: 'Reason for suspension', noteRequired: true, danger: true },
  reinstate: { title: 'Reinstate this doctor', button: 'Reinstate doctor', noteLabel: 'Internal note', noteRequired: false, danger: false },
  deactivate: { title: 'Deactivate this doctor', button: 'Deactivate permanently', noteLabel: 'Reason for deactivation', noteRequired: true, danger: true },
}

const actionsByStatus: Record<AdminDoctorDetail['status'], Action[]> = {
  Draft: [],
  PendingVerification: ['verify', 'reject'],
  Verified: ['suspend', 'deactivate'],
  Rejected: [],
  Suspended: ['reinstate', 'deactivate'],
  Deactivated: [],
}

export function AdminDoctorDetailPage() {
  const { id = '' } = useParams()
  const { data: doctor, error, reload } = useApi<AdminDoctorDetail>(`/admin/doctors/${id}`)
  const [message, setMessage] = useState<string | null>(null)
  const [action, setAction] = useState<Action | null>(null)

  if (error && !doctor) return <p className="alert alert-error" role="alert">{errorMessage(error)}</p>
  if (!doctor) return <Loading />
  const d = doctor

  return (
    <section className="panel">
      <title>{`Review ${d.fullName} · Smart Doctor`}</title>
      <p><Link to="/admin/doctors">← Back to doctor verification</Link></p>
      <header className="profile-header">
        <div>
          <h1>{d.fullName}</h1>
          <p className="meta">{d.specialty}{d.qualifications ? ` · ${d.qualifications}` : ''}</p>
        </div>
        <span className={`status status-${d.status}`}>{statusLabels[d.status]}</span>
      </header>

      {message && <p className="alert alert-success" role="status">{message}</p>}
      {d.possibleDuplicate && (
        <div className="alert alert-warning" role="note">
          <p><strong>Possible duplicate.</strong> Another account has submitted the same registration number.</p>
        </div>
      )}

      <h2>Professional registration</h2>
      <dl className="details">
        <div><dt>Medical Registration ID</dt><dd className="code">{d.medicalRegistrationId}</dd></div>
        <div><dt>Registering council</dt><dd>{d.medicalRegistrationAuthority}</dd></div>
        <div><dt>Doctor ID</dt><dd className="code">{d.doctorCode ?? 'Issued on verification'}</dd></div>
        <div><dt>Primary city</dt><dd>{d.primaryCityCode}</dd></div>
        <div><dt>Submitted</dt><dd>{formatDate(d.submittedAt)} ({d.submissionReference ?? '—'})</dd></div>
        {d.verifiedAt && <div><dt>Verified</dt><dd>{formatDate(d.verifiedAt)}{d.verificationReference ? ` · ${d.verificationReference}` : ''}</dd></div>}
        {d.rejectionReason && <div><dt>Rejection reason</dt><dd>{rejectionLabels[d.rejectionReason]}</dd></div>}
        {d.statusNote && <div><dt>Latest note</dt><dd>{d.statusNote}</dd></div>}
      </dl>

      <h2>Contact and clinic</h2>
      <dl className="details">
        <div><dt>Mobile</dt><dd>{d.mobileNumberMasked}</dd></div>
        <div><dt>Email</dt><dd>{d.email ?? '—'}</dd></div>
        <div><dt>Clinic</dt><dd>{d.clinicName ?? '—'}</dd></div>
        <div><dt>Address</dt><dd>{d.clinicAddress ?? '—'}</dd></div>
        <div><dt>Clinic phone</dt><dd>{d.clinicPublicPhone ?? '—'}</dd></div>
      </dl>

      <h2>Declarations</h2>
      {d.declarations.length === 0 ? <p>No declaration accepted yet.</p> : (
        <div className="table-wrap">
          <table>
            <thead><tr><th scope="col">Version</th><th scope="col">Accepted at</th><th scope="col">Submission</th><th scope="col">IP address</th></tr></thead>
            <tbody>
              {d.declarations.map((x) => (
                <tr key={x.submissionReference}>
                  <td>{x.version}</td><td>{formatDate(x.acceptedAt)}</td><td>{x.submissionReference}</td><td>{x.ipAddress ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {d.sameRegistrationNumber.length > 0 && (
        <>
          <h2>Other accounts with this registration number</h2>
          <ul>
            {d.sameRegistrationNumber.map((x) => (
              <li key={x.doctorId}>
                <Link to={`/admin/doctors/${x.doctorId}`}>{x.fullName}</Link> · {statusLabels[x.status]}{x.doctorCode ? ` · ${x.doctorCode}` : ''}
              </li>
            ))}
          </ul>
        </>
      )}

      <h2>Plan</h2>
      <PlanForm
        doctorId={d.doctorId}
        plan={d.plan}
        onDone={(text) => {
          setMessage(text)
          reload()
        }}
      />

      {actionsByStatus[d.status].length > 0 && (
        <div className="actions">
          {actionsByStatus[d.status].map((a) => (
            <button key={a} type="button" className={`button ${actionCopy[a].danger ? 'danger' : 'primary'}`} onClick={() => setAction(a)}>
              {actionCopy[a].button}
            </button>
          ))}
        </div>
      )}

      {action && (
        <ActionDialog
          action={action}
          doctor={d}
          onClose={() => setAction(null)}
          onDone={(text) => {
            setAction(null)
            setMessage(text)
            reload()
          }}
          onConflict={() => {
            setAction(null)
            setMessage('This record was changed by someone else. The latest version is shown; review it before acting again.')
            reload()
          }}
        />
      )}
    </section>
  )
}

function ActionDialog({
  action, doctor, onClose, onDone, onConflict,
}: {
  action: Action
  doctor: AdminDoctorDetail
  onClose: () => void
  onDone: (message: string) => void
  onConflict: () => void
}) {
  const dialogRef = useModalDialog()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({})
  const copy = actionCopy[action]

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const data = new FormData(event.currentTarget)
    const text = (name: string) => String(data.get(name) ?? '').trim() || null
    const body: Record<string, unknown> = { note: text('note'), rowVersion: doctor.rowVersion }
    if (action === 'verify') body.verificationReference = text('verificationReference')
    if (action === 'reject') body.reason = text('reason')

    setBusy(true)
    setError(null)
    try {
      const result = await api<{ status: string; doctorCode: string | null }>(`/admin/doctors/${doctor.doctorId}/${action}`, { method: 'POST', body })
      onDone(action === 'verify' ? `Verified. Doctor ID ${result.doctorCode} issued.` : `Status changed to ${result.status}.`)
    } catch (e) {
      if (e instanceof ApiError && e.status === 409 && e.message.includes('changed by someone else')) {
        onConflict()
        return
      }
      if (e instanceof ApiError) setFieldErrors(e.fieldErrors)
      setError(errorMessage(e))
      setBusy(false)
    }
  }

  return (
    <dialog ref={dialogRef} className="dialog" aria-labelledby="action-title" onClose={onClose}>
      <form method="dialog" onSubmit={handleSubmit}>
        <h2 id="action-title">{copy.title}</h2>
        <p>{doctor.fullName} · {doctor.medicalRegistrationAuthority} · {doctor.medicalRegistrationId}</p>
        <FormAlert message={error} />
        {action === 'verify' && (
          <TextField name="verificationReference" label="Verification evidence" optional maxLength={100}
            hint="For example the register lookup reference or document checked" errors={fieldErrors.verificationReference} />
        )}
        {action === 'reject' && (
          <SelectField name="reason" label="Reason" errors={fieldErrors.reason}
            options={(Object.keys(rejectionLabels) as RejectionReason[]).map((r) => ({ value: r, label: rejectionLabels[r] }))} />
        )}
        <TextAreaField name="note" label={copy.noteLabel} optional={!copy.noteRequired} maxLength={500}
          hint={action === 'reject' ? 'Required when the reason is Other. The doctor sees this note.' : undefined}
          errors={fieldErrors.note} />
        {action === 'deactivate' && <p className="alert alert-warning">Deactivation cannot be undone. The Doctor ID is never reused.</p>}
        <div className="actions">
          <button type="submit" className={`button ${copy.danger ? 'danger' : 'primary'}`} disabled={busy}>
            {busy ? 'Saving…' : copy.button}
          </button>
          <button type="button" className="button" onClick={() => dialogRef.current?.close()} disabled={busy}>Cancel</button>
        </div>
      </form>
    </dialog>
  )
}

function PlanForm({ doctorId, plan, onDone }: { doctorId: string; plan: DoctorPlan; onDone: (message: string) => void }) {
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const data = new FormData(event.currentTarget)
    const next = String(data.get('plan')) as DoctorPlan
    if (next === plan) return
    setBusy(true)
    setError(null)
    try {
      await api(`/admin/doctors/${doctorId}/plan`, { method: 'POST', body: { plan: next, note: String(data.get('planNote') ?? '').trim() || null } })
      onDone(`Plan changed to ${next}.`)
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="stack" onSubmit={handleSubmit} key={plan}>
      <FormAlert message={error} />
      <SelectField name="plan" label="Current plan" defaultValue={plan}
        options={(Object.keys(planLabels) as DoctorPlan[]).map((p) => ({ value: p, label: planLabels[p] }))} />
      <TextField name="planNote" label="Reason for change" optional maxLength={500} hint="Recorded in the audit log" />
      <div className="actions">
        <button type="submit" className="button" disabled={busy}>{busy ? 'Saving…' : 'Change plan'}</button>
      </div>
    </form>
  )
}
