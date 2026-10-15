import { type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { errorMessage } from '../../api/client'
import { statusLabels, type AdminDoctorRow, type DoctorStatus, type Paged } from '../../api/types'
import { useApi } from '../../api/useApi'
import { Loading } from '../../components/Layout'
import { formatDate } from '../../format'

const tabs: { status: DoctorStatus | ''; label: string }[] = [
  { status: 'PendingVerification', label: 'Pending' },
  { status: 'Verified', label: 'Verified' },
  { status: 'Rejected', label: 'Rejected' },
  { status: 'Suspended', label: 'Suspended' },
  { status: 'Deactivated', label: 'Deactivated' },
  { status: '', label: 'All submitted' },
]

export function AdminDoctorsPage() {
  const [params, setParams] = useSearchParams()
  const status = (params.get('status') ?? 'PendingVerification') as DoctorStatus | ''
  const q = params.get('q') ?? ''
  const page = Number(params.get('page') ?? '1') || 1

  const search = new URLSearchParams({ page: String(page) })
  if (status) search.set('status', status)
  if (q) search.set('q', q)
  const { data, error, loading } = useApi<Paged<AdminDoctorRow>>(`/admin/doctors?${search}`)

  function update(changes: Record<string, string>) {
    const next = new URLSearchParams(params)
    for (const [k, v] of Object.entries(changes)) next.set(k, v)
    if (!('page' in changes)) next.set('page', '1')
    setParams(next)
  }

  function handleSearch(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    update({ q: String(new FormData(event.currentTarget).get('q') ?? '').trim() })
  }

  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1

  return (
    <section>
      <title>Doctor verification · Smart Doctor</title>
      <h1>Doctor verification</h1>
      <nav className="tabs" aria-label="Filter by status">
        {tabs.map((t) => (
          <button key={t.label} type="button" className="tab" aria-current={status === t.status ? 'page' : undefined}
            onClick={() => update({ status: t.status })}>
            {t.label}
          </button>
        ))}
      </nav>
      <form role="search" className="inline-search" onSubmit={handleSearch} key={q}>
        <label htmlFor="admin-q" className="visually-hidden">Search doctors</label>
        <input id="admin-q" name="q" type="search" defaultValue={q} enterKeyHint="search" spellCheck={false}
          autoComplete="off" placeholder="Name, Doctor ID, registration number, specialty or city code" />
        <button type="submit" className="button">Search</button>
      </form>

      {error ? <p className="alert alert-error" role="alert">{errorMessage(error)}</p> : null}
      {loading && <Loading />}
      {data && !loading && (
        <>
          <p className="results-heading">{data.total} doctor{data.total === 1 ? '' : 's'}</p>
          {data.total > 0 && (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th scope="col">Name</th>
                    <th scope="col">Specialty</th>
                    <th scope="col">City</th>
                    <th scope="col">Submitted</th>
                    <th scope="col">Status</th>
                    <th scope="col">Doctor ID</th>
                    <th scope="col"><span className="visually-hidden">Actions</span></th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((d) => (
                    <tr key={d.doctorId}>
                      <td>
                        {d.fullName}
                        {d.possibleDuplicate && <span className="badge badge-warning">Possible duplicate</span>}
                      </td>
                      <td>{d.specialty}</td>
                      <td>{d.primaryCityCode}</td>
                      <td>{formatDate(d.submittedAt)}</td>
                      <td><span className={`status status-${d.status}`}>{statusLabels[d.status]}</span></td>
                      <td className="code">{d.doctorCode ?? '—'}</td>
                      <td><Link to={`/admin/doctors/${d.doctorId}`} className="button small">Review</Link></td>
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
        </>
      )}
    </section>
  )
}
