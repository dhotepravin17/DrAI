import { useSearchParams } from 'react-router-dom'
import { ApiError, errorMessage } from '../api/client'
import type { ClinicReport } from '../api/types'
import { useApi } from '../api/useApi'
import { Loading } from '../components/Layout'
import { addDays, formatDay, todayIst } from '../format'
import { useClinicScope } from './clinicScopeState'

const minutes = (value: number | null) => (value === null ? '—' : `${value} min`)
const sourceNames: Record<string, string> = { Online: 'Booked online', WalkIn: 'Walk-in', StaffBooked: 'Booked by staff' }

/** PRD §39: clinic reports for Pro and Premium plans. */
export function ReportsPage() {
  const scope = useClinicScope()
  const [params, setParams] = useSearchParams()
  const to = params.get('to') ?? todayIst()
  const from = params.get('from') ?? addDays(to, -29)
  const { data, error, loading } = useApi<ClinicReport>(scope.path(`/clinic/reports?from=${from}&to=${to}`))
  const upgradeNeeded = error instanceof ApiError && error.status === 403

  const tiles: [string, string][] = data ? [
    ['Appointments', String(data.appointments)],
    ['Completed', String(data.completed)],
    ['No-shows', String(data.noShows)],
    ['Missed a turn', String(data.missed)],
    ['Emergencies', String(data.emergencies)],
    ['Cancelled', String(data.cancelled)],
    ['Average consultation', minutes(data.averageConsultMinutes)],
    ['Average wait to be called', minutes(data.averageWaitMinutes)],
  ] : []

  return (
    <section className="stack">
      <title>Reports · Smart Doctor Queue</title>
      <h1>Reports</h1>
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
      {upgradeNeeded ? (
        <p className="alert alert-warning" role="note">{errorMessage(error)} Your appointment history is still available under History.</p>
      ) : error ? (
        <p className="alert alert-error" role="alert">{errorMessage(error)}</p>
      ) : null}
      {loading && !data && <Loading />}
      {data && (
        <>
          <ul className="stat-grid">
            {tiles.map(([label, value]) => (
              <li key={label} className="panel stat-tile">
                <span className="stat-value">{value}</span>
                <span>{label}</span>
              </li>
            ))}
          </ul>
          <h2>How patients joined</h2>
          {Object.keys(data.bySource).length === 0 ? <p>No appointments in this period.</p> : (
            <ul>
              {Object.entries(data.bySource).map(([source, count]) => (
                <li key={source}>{sourceNames[source] ?? source}: {count}</li>
              ))}
            </ul>
          )}
          <h2>By day</h2>
          {data.daily.length === 0 ? <p>No appointments in this period.</p> : (
            <div className="table-wrap">
              <table>
                <thead><tr><th scope="col">Date</th><th scope="col">Appointments</th><th scope="col">Completed</th><th scope="col">No-shows</th></tr></thead>
                <tbody>
                  {data.daily.map((d) => (
                    <tr key={d.date}><td>{formatDay(d.date)}</td><td>{d.appointments}</td><td>{d.completed}</td><td>{d.noShows}</td></tr>
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
