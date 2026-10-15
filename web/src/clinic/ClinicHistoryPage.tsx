import { useSearchParams } from 'react-router-dom'
import { errorMessage } from '../api/client'
import { appointmentStatusLabels, emergencyLabels, sourceLabels, type AppointmentStatus, type ClinicVisit } from '../api/queueTypes'
import { useApi } from '../api/useApi'
import { Loading } from '../components/Layout'
import { formatDay, formatTime } from '../format'
import { useClinicScope } from './clinicScopeState'

interface Paged<T> { items: T[]; page: number; pageSize: number; total: number }
const filterKeys = ['from', 'to', 'status', 'token', 'reference', 'patientName', 'mobile'] as const

export function ClinicHistoryPage() {
  const scope = useClinicScope()
  const [params, setParams] = useSearchParams()
  const query = new URLSearchParams()
  for (const k of [...filterKeys, 'page']) {
    const v = params.get(k)
    if (v) query.set(k, v)
  }
  const { data, error, loading } = useApi<Paged<ClinicVisit>>(scope.path(`/clinic/appointments?${query}`))
  const page = Number(params.get('page') ?? '1') || 1
  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1

  function apply(form: HTMLFormElement) {
    const d = new FormData(form)
    const next = new URLSearchParams()
    for (const k of filterKeys) {
      const v = String(d.get(k) ?? '').trim()
      if (v) next.set(k, v)
    }
    setParams(next)
  }

  function goTo(n: number) {
    const next = new URLSearchParams(params)
    next.set('page', String(n))
    setParams(next)
  }

  return (
    <section className="stack">
      <title>History · MediCare Plus</title>
      <h1>Appointment history</h1>
      <form className="panel filter-form" key={params.toString()} onSubmit={(e) => { e.preventDefault(); apply(e.currentTarget) }}>
        <label>From <input type="date" name="from" defaultValue={params.get('from') ?? ''} /></label>
        <label>To <input type="date" name="to" defaultValue={params.get('to') ?? ''} /></label>
        <label>Status
          <select name="status" defaultValue={params.get('status') ?? ''}>
            <option value="">Any</option>
            {(Object.keys(appointmentStatusLabels) as AppointmentStatus[]).map((s) => <option key={s} value={s}>{appointmentStatusLabels[s]}</option>)}
          </select>
        </label>
        <label>Token <input name="token" placeholder="T-012" defaultValue={params.get('token') ?? ''} autoComplete="off" /></label>
        <label>Reference <input name="reference" placeholder="APT-2026-000123" defaultValue={params.get('reference') ?? ''} autoComplete="off" spellCheck={false} /></label>
        <label>Patient name <input name="patientName" defaultValue={params.get('patientName') ?? ''} autoComplete="off" /></label>
        <label>Mobile <input name="mobile" type="tel" inputMode="tel" defaultValue={params.get('mobile') ?? ''} autoComplete="off" /></label>
        <div className="actions">
          <button type="submit" className="button primary">Search</button>
          <button type="button" className="button" onClick={() => setParams(new URLSearchParams())}>Clear</button>
        </div>
      </form>

      {error ? <p className="alert alert-error" role="alert">{errorMessage(error)}</p> : null}
      {loading && !data && <Loading />}
      {data && <p className="results-heading">{data.total} appointment{data.total === 1 ? '' : 's'}</p>}
      {data && data.total > 0 && (
        <div className="table-wrap">
          <table>
            <thead>
              <tr><th scope="col">Date</th><th scope="col">Token</th><th scope="col">Patient</th><th scope="col">Source</th><th scope="col">Status</th><th scope="col">History</th></tr>
            </thead>
            <tbody>
              {data.items.map((v) => (
                <tr key={v.appointmentId}>
                  <td>{formatDay(v.date)}<div className="hint">{v.sessionName}</div></td>
                  <td className="code">{v.token}<div className="hint">{v.reference}</div></td>
                  <td>
                    {v.patientName}
                    {v.priority === 'Emergency' && <div className="badge badge-danger">{v.emergencyReason ? emergencyLabels[v.emergencyReason] : 'Emergency'}</div>}
                  </td>
                  <td>{sourceLabels[v.source]}</td>
                  <td>{appointmentStatusLabels[v.status]}{v.missCount > 0 && <div className="hint">Missed {v.missCount}×</div>}</td>
                  <td>
                    <details>
                      <summary>{v.events.length} events</summary>
                      <ol className="event-list">
                        {v.events.map((e, i) => (
                          <li key={i}>{formatTime(e.occurredAt)} · {e.type}{e.reason ? ` — ${e.reason}` : ''}</li>
                        ))}
                      </ol>
                    </details>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {totalPages > 1 && (
        <nav className="pager" aria-label="Pages">
          <button type="button" className="button" disabled={page <= 1} onClick={() => goTo(page - 1)}>Previous</button>
          <span>Page {page} of {totalPages}</span>
          <button type="button" className="button" disabled={page >= totalPages} onClick={() => goTo(page + 1)}>Next</button>
        </nav>
      )}
    </section>
  )
}
