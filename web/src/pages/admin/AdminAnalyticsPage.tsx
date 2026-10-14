import { useSearchParams } from 'react-router-dom'
import { errorMessage } from '../../api/client'
import type { PlatformAnalytics } from '../../api/types'
import { useApi } from '../../api/useApi'
import { Loading } from '../../components/Layout'
import { addDays, todayIst } from '../../format'

const percent = (value: number | null) => (value === null ? '—' : `${Math.round(value * 1000) / 10}%`)
const measure = (value: number | null, unit: string) => (value === null ? '—' : `${value} ${unit}`)

/** PRD §4 and §38: platform metrics built from product events (internal IDs only). */
export function AdminAnalyticsPage() {
  const [params, setParams] = useSearchParams()
  const to = params.get('to') ?? todayIst()
  const from = params.get('from') ?? addDays(to, -29)
  const { data, error, loading } = useApi<PlatformAnalytics>(`/admin/analytics?from=${from}&to=${to}`)
  const m = data?.metrics

  const tiles: [string, string][] = m ? [
    ['Bookings', String(m.bookings)],
    ['Booked online', percent(m.onlineShare)],
    ['Booked for self', percent(m.forSelfShare)],
    ['Cancellation rate', percent(m.cancellationRate)],
    ['No-show rate', percent(m.noShowRate)],
    ['Average consultation', measure(m.averageConsultMinutes, 'min')],
    ['Average wait to be called', measure(m.averageWaitMinutes, 'min')],
    ['Notification failure rate', percent(m.notificationFailureRate)],
    ['Average verification time', measure(m.averageReviewHours, 'h')],
    ['Patients registered', String(m.patientsRegistered)],
    ['Family members added', String(m.familyMembersAdded)],
    ['Searches with no results', percent(m.zeroResultSearchShare)],
  ] : []

  return (
    <section className="stack">
      <title>Analytics · Smart Doctor Queue</title>
      <h1>Analytics</h1>
      <form
        className="inline-search"
        onSubmit={(e) => {
          e.preventDefault()
          const d = new FormData(e.currentTarget)
          setParams({ from: String(d.get('from')), to: String(d.get('to')) })
        }}
        key={`${from}:${to}`}
      >
        <label className="inline-field"><span>From</span><input type="date" name="from" defaultValue={from} required /></label>
        <label className="inline-field"><span>To</span><input type="date" name="to" defaultValue={to} required /></label>
        <button type="submit" className="button">Show</button>
      </form>
      {error ? <p className="alert alert-error" role="alert">{errorMessage(error)}</p> : null}
      {loading && !data && <Loading />}
      {data && m && (
        <>
          <ul className="stat-grid">
            {tiles.map(([label, value]) => (
              <li key={label} className="panel stat-tile">
                <span className="stat-value">{value}</span>
                <span>{label}</span>
              </li>
            ))}
          </ul>
          <h2>All events</h2>
          {data.events.length === 0 ? <p>No events in this period.</p> : (
            <div className="table-wrap">
              <table>
                <thead><tr><th scope="col">Event</th><th scope="col">Category</th><th scope="col">Count</th><th scope="col">Average value</th></tr></thead>
                <tbody>
                  {data.events.map((e) => (
                    <tr key={`${e.name}:${e.dimension ?? ''}`}>
                      <td className="code">{e.name}</td><td>{e.dimension ?? '—'}</td><td>{e.count}</td>
                      <td>{e.average === null ? '—' : Math.round(e.average * 10) / 10}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </section>
  )
}
