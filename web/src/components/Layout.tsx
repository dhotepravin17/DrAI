import { Link, NavLink, Navigate, Outlet, useLocation } from 'react-router-dom'
import type { ReactNode } from 'react'
import type { NotificationList } from '../api/queueTypes'
import { useApi } from '../api/useApi'
import { usePolling } from '../api/useQueueSignal'
import { useAuth } from '../auth/authState'
import { doctorHomePath } from '../auth/landing'

export function Layout() {
  const { me, token, signOut } = useAuth()
  const { pathname } = useLocation()
  const isAdmin = me?.roles.includes('SuperAdmin') ?? false
  const isPatient = Boolean(me?.patient)
  const hasClinic = me?.doctor?.status === 'Verified' || (me?.roles.includes('Receptionist') ?? false)
  // Doctors see only doctor items; the patient menu (and doctor search) is for everyone else. A new doctor filling in
  // step 2 has no doctor record until the form is first saved, so the registration page itself also uses the doctor menu.
  const doctor = me?.doctor ?? null
  const doctorMode = Boolean(doctor) || (Boolean(me) && !isAdmin && pathname.startsWith('/doctor/register'))
  const patientArea = isPatient && !doctorMode
  const doctorHome = doctor ? doctorHomePath(doctor) : '/doctor/register'
  const notifications = useApi<NotificationList>(me && isPatient ? '/notifications' : null)
  usePolling(notifications.reload, 60_000, Boolean(me && isPatient))
  const unread = notifications.data?.unread ?? 0

  return (
    <>
      <a className="skip-link" href="#main">Skip to content</a>
      <header className="site-header">
        <div className="container header-inner">
          <Link to={doctorMode ? doctorHome : '/'} className="brand">
            <span className="brand-mark" aria-hidden="true">+</span>
            Smart Doctor Queue
          </Link>
          <nav aria-label="Main">
            <ul className="nav-list">
              {!doctorMode && <li><NavLink to="/" end>Find a doctor</NavLink></li>}
              {patientArea && <li><NavLink to="/appointments">Appointments</NavLink></li>}
              {patientArea && <li><NavLink to="/visits">Visits</NavLink></li>}
              {patientArea && <li><NavLink to="/family">Family</NavLink></li>}
              {hasClinic && <li><NavLink to="/clinic">Clinic</NavLink></li>}
              {doctorMode && doctor?.status !== 'Verified' && <li><NavLink to="/doctor/register">Registration</NavLink></li>}
              {isAdmin && <li><NavLink to="/admin" end>Overview</NavLink></li>}
              {isAdmin && <li><NavLink to="/admin/doctors">Doctors</NavLink></li>}
              {isAdmin && <li><NavLink to="/admin/patients">Patients</NavLink></li>}
              {isAdmin && <li><NavLink to="/admin/queues">Queues</NavLink></li>}
              {isAdmin && <li><NavLink to="/admin/notifications">Failures</NavLink></li>}
              {isAdmin && <li><NavLink to="/admin/analytics">Analytics</NavLink></li>}
              {isAdmin && <li><NavLink to="/admin/audit">Audit</NavLink></li>}
              {isAdmin && <li><NavLink to="/admin/cities">Cities</NavLink></li>}
              {isPatient && (
                <li>
                  <NavLink to="/notifications" aria-label={unread ? `Notifications, ${unread} unread` : 'Notifications'}>
                    Alerts{unread > 0 && <span className="count-badge" aria-hidden="true">{unread}</span>}
                  </NavLink>
                </li>
              )}
              {patientArea && <li><NavLink to="/account">Account</NavLink></li>}
              <li>
                {token ? (
                  <button type="button" className="link-button" onClick={signOut}>
                    Sign out
                  </button>
                ) : (
                  <NavLink to="/sign-in">Sign in</NavLink>
                )}
              </li>
              {!token && <li><NavLink to="/register" className="button primary small">Register</NavLink></li>}
            </ul>
          </nav>
        </div>
      </header>
      <main id="main" className="container main">
        <Outlet />
      </main>
      <footer className="site-footer">
        <div className="container">Estimated times are approximate. In an emergency, go to the nearest hospital.</div>
      </footer>
    </>
  )
}

/** Public pages meant for patients (doctor search): a signed-in doctor goes to their own area instead. */
export function RedirectDoctors({ children }: { children: ReactNode }) {
  const { me } = useAuth()
  return me?.doctor ? <Navigate to={doctorHomePath(me.doctor)} replace /> : children
}

export function Loading({ label = 'Loading…' }: { label?: string }) {
  return <p className="loading" role="status">{label}</p>
}

/**
 * Signed-in pages. `patientOnly` pages (appointments, visits, family, account, booking) send doctors back to their
 * own area, so the doctor experience contains only doctor features.
 */
export function RequireAuth({ children, role, patientOnly }: { children: ReactNode; role?: 'SuperAdmin'; patientOnly?: boolean }) {
  const { token, me, loading, refreshMe } = useAuth()
  const location = useLocation()

  if (!token) {
    const next = encodeURIComponent(location.pathname + location.search)
    return <Navigate to={`/sign-in?next=${next}`} replace />
  }
  if (loading) return <Loading />
  if (!me) {
    return (
      <p className="alert alert-error" role="alert">
        We could not load your account.{' '}
        <button type="button" className="link-button" onClick={() => void refreshMe()}>Try again</button>
      </p>
    )
  }
  if (patientOnly && me.doctor) return <Navigate to={doctorHomePath(me.doctor)} replace />
  if (role && !me.roles.includes(role)) {
    return (
      <section className="panel">
        <h1>Not available</h1>
        <p>Your account does not have access to this page.</p>
      </section>
    )
  }
  return children
}
