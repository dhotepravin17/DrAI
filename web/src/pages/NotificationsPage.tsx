import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, ApiError, errorMessage } from '../api/client'
import type { NotificationList } from '../api/queueTypes'
import { useApi } from '../api/useApi'
import { Loading } from '../components/Layout'
import { formatDate } from '../format'
import { currentSubscription, disablePush, enablePush, needsHomeScreenInstall, pushSupported } from '../push'

export function PushToggle() {
  const supported = pushSupported()
  const [enabled, setEnabled] = useState<boolean | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!supported) return
    currentSubscription().then((s) => setEnabled(Boolean(s))).catch(() => setEnabled(false))
  }, [supported])

  async function toggle() {
    setBusy(true)
    setError(null)
    try {
      if (enabled) await disablePush()
      else await enablePush()
      setEnabled(!enabled)
    } catch (e) {
      setError(e instanceof ApiError || !(e instanceof Error) ? errorMessage(e) : e.message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="panel">
      <h2>Phone alerts</h2>
      {!supported ? (
        <p>This browser cannot show alerts when the app is closed. Keep your appointment page open to follow your turn.</p>
      ) : needsHomeScreenInstall() ? (
        <p>On iPhone or iPad, tap Share, then <strong>Add to Home Screen</strong>, and open the app from there to turn on alerts.</p>
      ) : (
        <>
          <p>Get an alert on this device when your turn is near and when you are called.</p>
          <button type="button" className={`button ${enabled ? '' : 'primary'}`} disabled={busy || enabled === null} onClick={() => void toggle()}>
            {busy ? 'Please wait…' : enabled ? 'Turn off alerts on this device' : 'Turn on alerts'}
          </button>
        </>
      )}
      {error && <p className="alert alert-error" role="alert">{error}</p>}
    </div>
  )
}

export function NotificationsPage() {
  const { data, error, loading, reload } = useApi<NotificationList>('/notifications')

  async function markRead(id: number) {
    await api(`/notifications/${id}/read`, { method: 'POST' }).catch(() => undefined)
    reload()
  }

  return (
    <section className="stack">
      <title>Notifications · Smart Doctor Queue</title>
      <div className="page-head">
        <h1>Notifications</h1>
        {data && data.unread > 0 && (
          <button type="button" className="button" onClick={() => void api('/notifications/read-all', { method: 'POST' }).then(reload)}>
            Mark all as read
          </button>
        )}
      </div>
      <PushToggle />
      {error ? <p className="alert alert-error" role="alert">{errorMessage(error)}</p> : null}
      {loading && !data && <Loading />}
      {data && data.items.length === 0 && <p className="panel">No notifications yet.</p>}
      <ul className="card-list">
        {data?.items.map((n) => (
          <li key={n.id} className={`panel notification${n.isRead ? '' : ' unread'}`}>
            <div>
              <h2 className="h3">{!n.isRead && <span className="dot" aria-label="Unread" />}{n.title}</h2>
              <p>{n.body}</p>
              <p className="hint">{formatDate(n.createdAt)}</p>
            </div>
            <div className="actions">
              {n.appointmentId && <Link className="button small" to={`/appointments/${n.appointmentId}`} onClick={() => void markRead(n.id)}>Open</Link>}
              {!n.isRead && <button type="button" className="button small" onClick={() => void markRead(n.id)}>Mark read</button>}
            </div>
          </li>
        ))}
      </ul>
    </section>
  )
}
