import { useState } from 'react'
import { api, errorMessage } from '../api/client'
import { relationshipLabels, type LinksResponse, type PersonDto, type Relationship } from '../api/queueTypes'
import { useApi } from '../api/useApi'
import { FamilyMemberFields } from '../components/FamilyMemberFields'
import { SelectField, TextField } from '../components/Fields'
import { Loading } from '../components/Layout'
import { ModalForm } from '../components/Modal'
import { formatDate } from '../format'
import { familyMemberBody } from '../people'

const linkRelationships = (Object.keys(relationshipLabels) as Relationship[]).filter((r) => r !== 'Self')

type Dialog = { kind: 'add' } | { kind: 'edit'; person: PersonDto } | { kind: 'remove'; person: PersonDto } | { kind: 'invite' }

export function FamilyPage() {
  const people = useApi<PersonDto[]>('/me/people')
  const links = useApi<LinksResponse>('/me/links')
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function act(path: string, method: 'POST' | 'DELETE', success: string) {
    setError(null)
    try {
      await api(path, { method })
      setMessage(success)
      links.reload()
      people.reload()
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  const family = people.data?.filter((p) => p.kind !== 'Self') ?? []

  return (
    <section className="stack">
      <title>Family · Valmedo</title>
      <div className="page-head">
        <h1>Family</h1>
        <button type="button" className="button primary" onClick={() => setDialog({ kind: 'add' })}>Add family member</button>
      </div>
      <p className="lead">Book for your children, spouse or parents. Each visit is recorded under the person who sees the doctor.</p>
      {message && <p className="alert alert-success" role="status">{message}</p>}
      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {people.loading && !people.data && <Loading />}
      {people.data && (
        <ul className="card-list">
          {family.length === 0 && <li className="panel">No family members yet.</li>}
          {family.map((p) => (
            <li key={p.patientId} className="panel person-card">
              <div>
                <h2 className="h3">{p.fullName}</h2>
                <p className="meta">
                  {relationshipLabels[p.relationship]}
                  {p.dateOfBirth ? ` · born ${p.dateOfBirth}` : ''}
                  {p.kind === 'LinkedAccount' ? ' · linked account' : p.isMinor ? ' · child' : ''}
                </p>
                <p className="hint">
                  {p.historyAccess === 'Full' ? 'You can see all of their visits.' : 'You can see only the visits you book for them.'}
                </p>
              </div>
              {p.kind === 'FamilyProfile' && (
                <div className="actions">
                  <button type="button" className="button small" onClick={() => setDialog({ kind: 'edit', person: p })}>Edit</button>
                  <button type="button" className="button small" onClick={() => setDialog({ kind: 'remove', person: p })}>Remove</button>
                </div>
              )}
            </li>
          ))}
        </ul>
      )}

      <section className="panel">
        <div className="page-head">
          <h2>Shared visit history</h2>
          <button type="button" className="button" onClick={() => setDialog({ kind: 'invite' })}>Ask an adult to share</button>
        </div>
        <p className="hint">
          Booking for an adult does not show you their other visits. They can accept a request from their own account, and stop sharing at any time.
        </p>
        {links.data && (
          <>
            {links.data.received.length > 0 && (
              <>
                <h3>Requests for you</h3>
                <ul className="card-list">
                  {links.data.received.map((l) => (
                    <li key={l.linkId} className="subpanel person-card">
                      <span><strong>{l.otherPartyName}</strong> ({relationshipLabels[l.relationship]}) wants to see your visit history.</span>
                      <div className="actions">
                        <button type="button" className="button primary small" onClick={() => void act(`/me/links/${l.linkId}/accept`, 'POST', `You now share your visits with ${l.otherPartyName}.`)}>Accept</button>
                        <button type="button" className="button small" onClick={() => void act(`/me/links/${l.linkId}/decline`, 'POST', 'Request declined.')}>Decline</button>
                      </div>
                    </li>
                  ))}
                </ul>
              </>
            )}
            <h3>People who can see your visits</h3>
            {links.data.sharingMyHistory.length === 0 ? <p>Nobody.</p> : (
              <ul className="card-list">
                {links.data.sharingMyHistory.map((l) => (
                  <li key={l.linkId} className="subpanel person-card">
                    <span><strong>{l.otherPartyName}</strong> since {formatDate(l.respondedAt)}</span>
                    <button type="button" className="button small" onClick={() => void act(`/me/links/${l.linkId}`, 'DELETE', `${l.otherPartyName} can no longer see your visits.`)}>Stop sharing</button>
                  </li>
                ))}
              </ul>
            )}
            {links.data.sent.length > 0 && (
              <>
                <h3>Your requests</h3>
                <ul className="card-list">
                  {links.data.sent.map((l) => (
                    <li key={l.linkId} className="subpanel person-card">
                      <span>{l.otherPartyName} ({l.inviteeMobileMasked}) · {l.status}</span>
                      {(l.status === 'Pending' || l.status === 'Active') && (
                        <button type="button" className="button small" onClick={() => void act(`/me/links/${l.linkId}`, 'DELETE', 'Request withdrawn.')}>
                          {l.status === 'Pending' ? 'Withdraw' : 'Remove'}
                        </button>
                      )}
                    </li>
                  ))}
                </ul>
              </>
            )}
          </>
        )}
      </section>

      {(dialog?.kind === 'add' || dialog?.kind === 'edit') && (
        <ModalForm
          title={dialog.kind === 'add' ? 'Add a family member' : `Edit ${dialog.person.fullName}`}
          submitLabel={dialog.kind === 'add' ? 'Add' : 'Save'}
          onClose={() => setDialog(null)}
          onSubmit={async (data) => {
            const body = familyMemberBody(data)
            if (dialog.kind === 'add') await api('/me/family', { method: 'POST', body })
            else await api(`/me/family/${dialog.person.patientId}`, { method: 'PUT', body })
            setMessage(dialog.kind === 'add' ? `${body.fullName} added.` : 'Changes saved.')
            people.reload()
          }}
        >
          {(errors) => <FamilyMemberFields errors={errors} person={dialog.kind === 'edit' ? dialog.person : undefined} />}
        </ModalForm>
      )}

      {dialog?.kind === 'remove' && (
        <ModalForm title={`Remove ${dialog.person.fullName}?`} submitLabel="Remove" danger onClose={() => setDialog(null)}
          onSubmit={async () => {
            await api(`/me/family/${dialog.person.patientId}`, { method: 'DELETE' })
            setMessage(`${dialog.person.fullName} removed. Their past visits are kept in your history.`)
            people.reload()
          }}>
          {() => <p>They will no longer appear when you book. Completed visits stay in the history.</p>}
        </ModalForm>
      )}

      {dialog?.kind === 'invite' && (
        <ModalForm title="Ask to see an adult's visit history" submitLabel="Send request" onClose={() => setDialog(null)}
          onSubmit={async (data) => {
            await api('/me/links', { method: 'POST', body: { mobileNumber: data.get('mobileNumber'), relationship: data.get('relationship') } })
            setMessage('Request sent. They need to accept it from their own account.')
            links.reload()
          }}>
          {(errors) => (
            <>
              <TextField name="mobileNumber" label="Their mobile number" type="tel" inputMode="tel" autoComplete="off" errors={errors.mobileNumber} />
              <SelectField name="relationship" label="Relationship to you" errors={errors.relationship}
                options={linkRelationships.map((r) => ({ value: r, label: relationshipLabels[r] }))} />
            </>
          )}
        </ModalForm>
      )}
    </section>
  )
}
