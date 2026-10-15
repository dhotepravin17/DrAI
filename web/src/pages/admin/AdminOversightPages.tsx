import { useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { api, errorMessage } from '../../api/client'
import {
  appointmentStatusLabels, emergencyLabels, sourceLabels, type AdminOverview, type AdminPatientHistory, type AdminPatientRow,
  type AdminQueueRow, type AuditRow, type FailureRow,
} from '../../api/queueTypes'
import type { Paged } from '../../api/types'
import { useApi } from '../../api/useApi'
import { TextField } from '../../components/Fields'
import { Loading } from '../../components/Layout'
import { ModalForm } from '../../components/Modal'
import { formatDate, formatDay, formatTime, todayIst } from '../../format'

export function AdminOverviewPage() {
  const { data, error } = useApi<AdminOverview>('/admin/overview')
  const tiles: [string, number | undefined, string][] = [
    ['Doctors awaiting verification', data?.pendingDoctors, '/admin/doctors'],
    ['Verified doctors', data?.verifiedDoctors, '/admin/doctors?status=Verified'],
    ['Appointment sessions open now', data?.openQueuesToday, '/admin/queues'],
    ['Appointments today', data?.appointmentsToday, '/admin/queues'],
    ['Notification failures (24 h)', data?.notificationFailures24h, '/admin/notifications'],
    ['Notifications waiting to send', data?.outboxBacklog, '/admin/notifications'],
  ]
  return (
    <section className="stack">
      <title>Platform overview · Smart Doctor Queue</title>
      <h1>Platform overview</h1>
      {error ? <p className="alert alert-error">{errorMessage(error)}</p> : null}
      <ul className="stat-grid">
        {tiles.map(([label, value, to]) => (
          <li key={label}>
            <Link to={to} className="panel stat-tile">
              <span className="stat-value">{value ?? '…'}</span>
              <span>{label}</span>
            </Link>
          </li>
        ))}
      </ul>
    </section>
  )
}

export function AdminPatientsPage() {
  const [params, setParams] = useSearchParams()
  const q = params.get('q') ?? ''
  const results = useApi<Paged<AdminPatientRow>>(q.length >= 2 ? `/admin/patients?q=${encodeURIComponent(q)}` : null)
  const [target, setTarget] = useState<AdminPatientRow | null>(null)
  const [history, setHistory] = useState<AdminPatientHistory | null>(null)

  function search(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setParams({ q: String(new FormData(event.currentTarget).get('q') ?? '').trim() })
    setHistory(null)
  }

  return (
    <section className="stack">
      <title>Patients · Smart Doctor Queue</title>
      <h1>Patients</h1>
      <form role="search" className="inline-search" onSubmit={search} key={q}>
        <label htmlFor="patient-q" className="visually-hidden">Search patients</label>
        <input id="patient-q" name="q" type="search" defaultValue={q} placeholder="Name or mobile number" autoComplete="off" spellCheck={false} />
        <button type="submit" className="button">Search</button>
      </form>
      {results.error ? <p className="alert alert-error">{errorMessage(results.error)}</p> : null}
      {results.loading && <Loading />}
      {results.data && (
        <div className="table-wrap">
          <table>
            <thead><tr><th scope="col">Name</th><th scope="col">Type</th><th scope="col">Account mobile</th><th scope="col">Appointments</th><th scope="col"><span className="visually-hidden">Actions</span></th></tr></thead>
            <tbody>
              {results.data.items.map((p) => (
                <tr key={p.patientId}>
                  <td>{p.fullName}</td><td>{p.kind}</td><td>{p.accountMobileMasked ?? '—'}</td><td>{p.appointments}</td>
                  <td><button type="button" className="button small" onClick={() => setTarget(p)}>Open history</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {history && (
        <div className="panel">
          <h2>{history.fullName}</h2>
          <p className="meta">{history.kind}{history.dateOfBirth ? ` · born ${history.dateOfBirth}` : ''} · {history.accountMobileMasked ?? 'no mobile'} · registered {formatDate(history.createdAt)}</p>
          <p className="hint">This access was recorded in the audit log.</p>
          <div className="table-wrap">
            <table>
              <thead><tr><th scope="col">Date</th><th scope="col">Doctor</th><th scope="col">Token</th><th scope="col">Status</th><th scope="col">Events</th></tr></thead>
              <tbody>
                {history.appointments.map((a) => (
                  <tr key={a.appointmentId}>
                    <td>{formatDay(a.date)} {a.sessionName}<div className="hint">{a.reference}</div></td>
                    <td>{a.doctorName}<div className="hint">{a.doctorCode}</div></td>
                    <td className="code">{a.token}<div className="hint">{sourceLabels[a.source]}</div></td>
                    <td>
                      {appointmentStatusLabels[a.status]}
                      {a.priority === 'Emergency' && <div className="badge badge-danger">{a.emergencyReason ? emergencyLabels[a.emergencyReason] : 'Emergency'}</div>}
                    </td>
                    <td>
                      <details><summary>{a.events.length}</summary>
                        <ol className="event-list">{a.events.map((e, i) => <li key={i}>{formatDate(e.occurredAt)} · {e.type}{e.reason ? ` — ${e.reason}` : ''}</li>)}</ol>
                      </details>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {target && (
        <ModalForm title={`Open ${target.fullName}'s history`} submitLabel="Open history" onClose={() => setTarget(null)}
          onSubmit={async (data) => {
            const query = new URLSearchParams({ purpose: String(data.get('purpose') ?? '') })
            const ticket = String(data.get('ticket') ?? '').trim()
            if (ticket) query.set('ticket', ticket)
            setHistory(await api<AdminPatientHistory>(`/admin/patients/${target.patientId}/history?${query}`))
          }}>
          {(errors) => (
            <>
              <p>Patient history is private. State why you need it; your access is recorded with this reason.</p>
              <TextField name="purpose" label="Purpose" minLength={5} maxLength={200} placeholder="Support ticket about a missed booking" errors={errors.purpose} />
              <TextField name="ticket" label="Ticket reference" optional maxLength={50} />
            </>
          )}
        </ModalForm>
      )}
    </section>
  )
}

export function AdminQueuesPage() {
  const [date, setDate] = useState(todayIst)
  const { data, error, loading, reload } = useApi<AdminQueueRow[]>(`/admin/queues?date=${date}`)
  return (
    <section className="stack">
      <title>Appointments · Smart Doctor Queue</title>
      <div className="page-head">
        <h1>Appointments</h1>
        <div className="inline-field">
          <input type="date" aria-label="Date" value={date} onChange={(e) => e.target.value && setDate(e.target.value)} />
          <button type="button" className="button small" onClick={reload}>Refresh</button>
        </div>
      </div>
      {error ? <p className="alert alert-error">{errorMessage(error)}</p> : null}
      {loading && !data && <Loading />}
      {data && data.length === 0 && <p className="panel">No sessions on {formatDay(date)}.</p>}
      {data && data.length > 0 && (
        <div className="table-wrap">
          <table>
            <thead><tr><th scope="col">Doctor</th><th scope="col">Session</th><th scope="col">State</th><th scope="col">Now serving</th><th scope="col">Booked</th><th scope="col">Waiting</th><th scope="col">Seen</th><th scope="col">Missed</th></tr></thead>
            <tbody>
              {data.map((q) => (
                <tr key={q.sessionId}>
                  <td>{q.doctorName}<div className="hint">{q.doctorCode}</div></td>
                  <td>{q.name}</td>
                  <td><span className={`status status-${q.state}`}>{q.state}</span></td>
                  <td className="code">{q.nowServing ?? '—'}</td>
                  <td>{q.booked}</td><td>{q.waiting}</td><td>{q.completed}</td><td>{q.missed}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}

export function AdminNotificationsPage() {
  const [page, setPage] = useState(1)
  const { data, error, loading } = useApi<Paged<FailureRow>>(`/admin/notification-failures?page=${page}`)
  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1
  return (
    <section className="stack">
      <title>Notification failures · Smart Doctor Queue</title>
      <h1>Notification failures</h1>
      {error ? <p className="alert alert-error">{errorMessage(error)}</p> : null}
      {loading && !data && <Loading />}
      {data && data.total === 0 && <p className="panel">No failed notifications.</p>}
      {data && data.total > 0 && (
        <div className="table-wrap">
          <table>
            <thead><tr><th scope="col">When</th><th scope="col">Event</th><th scope="col">Channel</th><th scope="col">Error</th><th scope="col">Appointment</th></tr></thead>
            <tbody>
              {data.items.map((f) => (
                <tr key={f.id}>
                  <td>{formatDate(f.createdAt)}</td><td>{f.eventType}</td><td>{f.channel}</td><td>{f.error ?? '—'}</td>
                  <td className="code">{f.appointmentId?.slice(0, 8) ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {totalPages > 1 && (
        <nav className="pager" aria-label="Pages">
          <button type="button" className="button" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button>
          <span>Page {page} of {totalPages}</span>
          <button type="button" className="button" disabled={page >= totalPages} onClick={() => setPage(page + 1)}>Next</button>
        </nav>
      )}
    </section>
  )
}

export function AdminAuditPage() {
  const [params, setParams] = useSearchParams()
  const query = new URLSearchParams()
  for (const k of ['entityType', 'entityId', 'page']) {
    const v = params.get(k)
    if (v) query.set(k, v)
  }
  const { data, error, loading } = useApi<Paged<AuditRow>>(`/admin/audit-logs?${query}`)
  const page = Number(params.get('page') ?? '1') || 1
  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1

  return (
    <section className="stack">
      <title>Audit log · Smart Doctor Queue</title>
      <h1>Audit log</h1>
      <form className="inline-search" key={params.toString()} onSubmit={(e) => {
        e.preventDefault()
        const d = new FormData(e.currentTarget)
        const next = new URLSearchParams()
        for (const k of ['entityType', 'entityId']) {
          const v = String(d.get(k) ?? '').trim()
          if (v) next.set(k, v)
        }
        setParams(next)
      }}>
        <select name="entityType" aria-label="Record type" defaultValue={params.get('entityType') ?? ''}>
          <option value="">All records</option>
          {['Doctor', 'Patient', 'QueueSession', 'User', 'CityCode'].map((t) => <option key={t} value={t}>{t}</option>)}
        </select>
        <input name="entityId" aria-label="Record id" placeholder="Record id" defaultValue={params.get('entityId') ?? ''} spellCheck={false} />
        <button type="submit" className="button">Filter</button>
      </form>
      {error ? <p className="alert alert-error">{errorMessage(error)}</p> : null}
      {loading && !data && <Loading />}
      {data && (
        <div className="table-wrap">
          <table>
            <thead><tr><th scope="col">When</th><th scope="col">Action</th><th scope="col">Record</th><th scope="col">Purpose</th><th scope="col">Details</th><th scope="col">By</th></tr></thead>
            <tbody>
              {data.items.map((a) => (
                <tr key={a.id}>
                  <td>{formatDay(a.occurredAt.slice(0, 10))} {formatTime(a.occurredAt)}</td>
                  <td>{a.action}</td>
                  <td>{a.entityType}<div className="hint code">{a.entityId.slice(0, 13)}</div></td>
                  <td>{a.purpose ?? '—'}</td>
                  <td>{a.details ?? '—'}</td>
                  <td className="code hint">{a.actorUserId?.slice(0, 8) ?? 'system'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {totalPages > 1 && (
        <nav className="pager" aria-label="Pages">
          <button type="button" className="button" disabled={page <= 1} onClick={() => setParams({ ...Object.fromEntries(params), page: String(page - 1) })}>Previous</button>
          <span>Page {page} of {totalPages}</span>
          <button type="button" className="button" disabled={page >= totalPages} onClick={() => setParams({ ...Object.fromEntries(params), page: String(page + 1) })}>Next</button>
        </nav>
      )}
    </section>
  )
}
