import { Link, useSearchParams } from 'react-router-dom'
import { errorMessage } from '../api/client'
import { appointmentStatusLabels, sourceLabels, type PersonDto, type Visit } from '../api/queueTypes'
import { useApi } from '../api/useApi'
import { Loading } from '../components/Layout'
import { formatDay } from '../format'
import { personLabel } from '../people'

interface Paged<T> { items: T[]; page: number; pageSize: number; total: number }

const tone: Record<string, string> = { Completed: 'success', Cancelled: 'danger', NoShow: 'danger', Missed: 'warning' }

export function VisitsPage() {
  const [params, setParams] = useSearchParams()
  const patientId = params.get('person') ?? ''
  const page = Number(params.get('page') ?? '1') || 1
  const people = useApi<PersonDto[]>('/me/people')
  const query = new URLSearchParams({ page: String(page) })
  if (patientId) query.set('patientId', patientId)
  const { data, error, loading } = useApi<Paged<Visit>>(`/me/visits?${query}`)
  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1

  function update(next: Record<string, string>) {
    const p = new URLSearchParams(params)
    for (const [k, v] of Object.entries(next)) {
      if (v) p.set(k, v)
      else p.delete(k)
    }
    setParams(p)
  }

  return (
    <section className="stack">
      <title>My visits · MediCare Plus</title>
      <h1>My visits</h1>
      <div className="field inline-field">
        <label htmlFor="person">Person</label>
        <select id="person" value={patientId} onChange={(e) => update({ person: e.target.value, page: '' })}>
          <option value="">All people I manage</option>
          {people.data?.map((p) => <option key={p.patientId} value={p.patientId}>{personLabel(p)}</option>)}
        </select>
      </div>
      {error ? <p className="alert alert-error" role="alert">{errorMessage(error)}</p> : null}
      {loading && !data && <Loading />}
      {data && data.total === 0 && <p className="panel">No visits yet.</p>}
      {data && data.total > 0 && (
        <div className="table-wrap">
          <table>
            <thead>
              <tr><th scope="col">Date</th><th scope="col">Patient</th><th scope="col">Doctor</th><th scope="col">Token</th><th scope="col">Status</th><th scope="col">Reference</th></tr>
            </thead>
            <tbody>
              {data.items.map((v) => (
                <tr key={v.appointmentId}>
                  <td>{formatDay(v.date)}<div className="hint">{v.sessionName}</div></td>
                  <td>{v.patientName}{!v.bookedByMe && <div className="hint">{sourceLabels[v.source]}</div>}</td>
                  <td>
                    {v.doctorCode ? <Link to={`/doctors/${v.doctorCode}`}>{v.doctorName}</Link> : v.doctorName}
                    <div className="hint">{v.clinicName}</div>
                  </td>
                  <td className="code">{v.token}</td>
                  <td>
                    <span className={`status tone-${tone[v.status] ?? 'info'}`}>{appointmentStatusLabels[v.status]}</span>
                    {v.followUpNote && <div className="hint">Note: {v.followUpNote}</div>}
                  </td>
                  <td><Link className="code" to={`/appointments/${v.appointmentId}`}>{v.reference}</Link></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {totalPages > 1 && (
        <nav className="pager" aria-label="Pages">
          <button type="button" className="button" disabled={page <= 1} onClick={() => update({ page: String(page - 1) })}>Previous</button>
          <span>Page {page} of {totalPages}</span>
          <button type="button" className="button" disabled={page >= totalPages} onClick={() => update({ page: String(page + 1) })}>Next</button>
        </nav>
      )}
    </section>
  )
}
