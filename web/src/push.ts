import { api } from './api/client'

export function pushSupported(): boolean {
  return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window
}

/** iOS only delivers web push to a PWA added to the Home Screen (iOS 16.4+). */
export function needsHomeScreenInstall(): boolean {
  const ios = /iPad|iPhone|iPod/.test(navigator.userAgent)
  const standalone = window.matchMedia('(display-mode: standalone)').matches
  return ios && !standalone
}

export async function registerServiceWorker() {
  if (!('serviceWorker' in navigator)) return
  try {
    await navigator.serviceWorker.register('/sw.js')
  } catch {
    // Alerts stay in-app only when the service worker cannot be registered.
  }
}

export async function currentSubscription(): Promise<PushSubscription | null> {
  if (!pushSupported()) return null
  const registration = await navigator.serviceWorker.ready
  return registration.pushManager.getSubscription()
}

function base64UrlToBytes(value: string): Uint8Array<ArrayBuffer> {
  const padded = value.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(value.length / 4) * 4, '=')
  const binary = atob(padded)
  const bytes = new Uint8Array(new ArrayBuffer(binary.length))
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i)
  return bytes
}

export async function enablePush(): Promise<void> {
  const { publicKey } = await api<{ publicKey: string | null }>('/notifications/push-key')
  if (!publicKey) throw new Error('Phone alerts are not available on this server yet. You will still see alerts in the app.')
  const permission = await Notification.requestPermission()
  if (permission !== 'granted') throw new Error('Alerts are blocked. Allow notifications for this site in your browser settings.')

  const registration = await navigator.serviceWorker.ready
  const subscription =
    (await registration.pushManager.getSubscription()) ??
    (await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: base64UrlToBytes(publicKey) }))
  const json = subscription.toJSON()
  await api('/notifications/push-subscriptions', { method: 'POST', body: { endpoint: json.endpoint, keys: json.keys } })
}

export async function disablePush(): Promise<void> {
  const subscription = await currentSubscription()
  if (!subscription) return
  await api(`/notifications/push-subscriptions?endpoint=${encodeURIComponent(subscription.endpoint)}`, { method: 'DELETE' })
  await subscription.unsubscribe()
}
