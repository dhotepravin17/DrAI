import { useEffect, useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { errorMessage, loadReference } from '../api/client'
import type { DoctorCard, Paged, ReferenceData } from '../api/types'
import { useApi } from '../api/useApi'
import { useAuth } from '../auth/authState'
import { Loading } from '../components/Layout'

export function HomePage() {
  const { token } = useAuth()
  const [params, setParams] = useSearchParams()
  const [reference, setReference] = useState<ReferenceData | null>(null)

  const q = params.get('q') ?? ''
  const city = params.get('city') ?? ''
  const specialty = params.get('specialty') ?? ''
  const page = Number(params.get('page') ?? '1') || 1

  const search = new URLSearchParams()
  if (q) search.set('q', q)
  if (city) search.set('city', city)
  if (specialty) search.set('specialty', specialty)
  const hasQuery = search.size > 0
  search.set('page', String(page))

  // With no search text or filters ("All cities", "All specialties") this lists every verified doctor.
  const searchResult = useApi<Paged<DoctorCard>>(`/doctors/search?${search}`)
  const result = !searchResult.loading ? searchResult.data ?? null : null
  const loading = searchResult.loading
  const error = searchResult.error ? errorMessage(searchResult.error) : null

  useEffect(() => {
    loadReference().then(setReference).catch(() => setReference(null))
  }, [])

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const data = new FormData(event.currentTarget)
    const next = new URLSearchParams()
    for (const key of ['q', 'city', 'specialty']) {
      const value = String(data.get(key) ?? '').trim()
      if (value) next.set(key, value)
    }
    setParams(next)
  }

  function goToPage(n: number) {
    const next = new URLSearchParams(params)
    next.set('page', String(n))
    setParams(next)
  }

  const totalPages = result ? Math.max(1, Math.ceil(result.total / result.pageSize)) : 1

  return (
    <>
      <title>Find a doctor · Smart Doctor Queue</title>
      <section className="hero">
        <h1>Book your doctor's appointment from home</h1>
        <p className="lead">Find your doctor, see today's clinic status, and get alerted when your turn is close.</p>
        {!token && (
          <div className="register-banner">
            <p>New to Smart Doctor Queue?</p>
            <Link to="/register/patient" className="button primary">Register as a patient</Link>
            <Link to="/register/doctor" className="button">Join as a doctor</Link>
          </div>
        )}
        <form role="search" method="get" className="search-form" onSubmit={handleSubmit} key={`${q}|${city}|${specialty}`}>
          <div className="field search-main">
            <label htmlFor="q">Search doctors</label>
            <input
              id="q"
              name="q"
              type="search"
              defaultValue={q}
              placeholder="Search doctor by name, Doctor ID, city or specialty"
              enterKeyHint="search"
              autoComplete="off"
              autoCorrect="off"
              autoCapitalize="off"
              spellCheck={false}
            />
          </div>
          <div className="search-filters">
            <div className="field">
              <label htmlFor="city">City</label>
              <select id="city" name="city" defaultValue={city}>
                <option value="">All cities</option>
                {reference?.cities.map((c) => <option key={c.code} value={c.code}>{c.name}</option>)}
              </select>
            </div>
            <div className="field">
              <label htmlFor="specialty">Specialty</label>
              <select id="specialty" name="specialty" defaultValue={specialty}>
                <option value="">All specialties</option>
                {reference?.specialties.map((s) => <option key={s} value={s}>{s}</option>)}
              </select>
            </div>
            <button type="submit" className="button primary">Search</button>
          </div>
        </form>
      </section>

      <section aria-live="polite" aria-busy={loading}>
        {error && <p className="alert alert-error" role="alert">{error}</p>}
        {loading && <Loading label="Searching…" />}
        {!loading && result && (
          <>
            <h2 className="results-heading">
              {result.total === 0
                ? 'No doctors found'
                : hasQuery
                  ? `${result.total} doctor${result.total === 1 ? '' : 's'} found`
                  : `All doctors (${result.total})`}
            </h2>
            {result.total === 0 && (
              <p>
                {hasQuery
                  ? 'Check the spelling, try the full Doctor ID such as NGP-Dr-0001, or remove a filter.'
                  : 'No verified doctors are listed yet.'}
              </p>
            )}
            <ul className="doctor-list">
              {result.items.map((d) => (
                <li key={d.doctorCode} className="doctor-card">
                  <div>
                    <h3><Link to={`/doctors/${d.doctorCode}`}>{d.fullName}</Link></h3>
                    <p className="meta">
                      <span className="code">{d.doctorCode}</span> · {d.specialty}
                      {d.qualifications ? ` · ${d.qualifications}` : ''}
                    </p>
                    {d.clinic && <p className="clinic">{d.clinic.name}, {d.clinic.address}, {d.cityName}</p>}
                  </div>
                  <Link to={`/doctors/${d.doctorCode}`} className="button">View profile</Link>
                </li>
              ))}
            </ul>
            {totalPages > 1 && (
              <nav className="pager" aria-label="Search result pages">
                <button type="button" className="button" disabled={page <= 1} onClick={() => goToPage(page - 1)}>Previous</button>
                <span>Page {page} of {totalPages}</span>
                <button type="button" className="button" disabled={page >= totalPages} onClick={() => goToPage(page + 1)}>Next</button>
              </nav>
            )}
          </>
        )}
      </section>
    </>
  )
}
