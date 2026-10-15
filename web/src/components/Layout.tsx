import { Link, NavLink, Navigate, Outlet, useLocation } from 'react-router-dom'
import type { ReactNode } from 'react'
import type { NotificationList } from '../api/queueTypes'
import { useApi } from '../api/useApi'
import { usePolling } from '../api/useQueueSignal'
import { useAuth } from '../auth/authState'

export function Layout() {
  const { me, token, signOut } = useAuth()
  const isAdmin = me?.roles.includes('SuperAdmin') ?? false
  const isPatient = Boolean(me?.patient)
  const hasClinic = me?.doctor?.status === 'Verified' || (me?.roles.includes('Receptionist') ?? false)
  const notifications = useApi<NotificationList>(me && isPatient ? '/notifications' : null)
  usePolling(notifications.reload, 60_000, Boolean(me && isPatient))
  const unread = notifications.data?.unread ?? 0

  return (
    <>
      <a className="skip-link" href="#main">Skip to content</a>
      <header className="site-header">
        <div className="container header-inner">
          <Link to="/" className="brand">
            <span className="brand-mark" aria-hidden="true">+</span>
            Smart Doctor Queue
          </Link>
          <nav aria-label="Main">
            <ul className="nav-list">
              <li><NavLink to="/" end>Find a doctor</NavLink></li>
              {isPatient && <li><NavLink to="/appointments">Appointments</NavLink></li>}
              {isPatient && <li><NavLink to="/visits">Visits</NavLink></li>}
              {isPatient && <li><NavLink to="/family">Family</NavLink></li>}
              {hasClinic && <li><NavLink to="/clinic">Clinic</NavLink></li>}
              {token && me && !isAdmin && !me.doctor && !hasClinic && <li><NavLink to="/doctor/register">Register as a doctor</NavLink></li>}
              {me?.doctor && me.doctor.status !== 'Verified' && <li><NavLink to="/doctor/register">Registration</NavLink></li>}
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
              {isPatient && <li><NavLink to="/account">Account</NavLink></li>}
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

export function Loading({ label = 'Loading…' }: { label?: string }) {
  return <p className="loading" role="status">{label}</p>
}

export function RequireAuth({ children, role }: { children: ReactNode; role?: 'SuperAdmin' }) {
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
