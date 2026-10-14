import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { useEffect, useRef, useState } from 'react'
import { getApiToken } from './client'

/**
 * Calls `onChange` whenever the server signals that a queue session changed.
 * Signals carry no data; callers re-read their own view through the API.
 * Returns whether the live connection is up so the page can fall back to polling.
 */
export function useQueueSignal(sessionId: string | null | undefined, onChange: () => void) {
  const callback = useRef(onChange)
  const [connected, setConnected] = useState(false)

  useEffect(() => {
    callback.current = onChange
  })

  useEffect(() => {
    if (!sessionId) return
    let cancelled = false
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/queue', { accessTokenFactory: () => getApiToken() ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    const join = () => connection.invoke('JoinSession', sessionId)
    connection.on('queueChanged', () => callback.current())
    connection.onreconnecting(() => setConnected(false))
    connection.onreconnected(async () => {
      await join()
      setConnected(true)
      callback.current()
    })
    connection.onclose(() => setConnected(false))

    connection
      .start()
      .then(() => (cancelled ? undefined : join()))
      .then(() => {
        if (!cancelled) setConnected(true)
      })
      .catch(() => {
        if (!cancelled) setConnected(false)
      })

    return () => {
      cancelled = true
      void connection.stop()
    }
  }, [sessionId])

  return connected
}

/** Calls `tick` every `intervalMs` while `enabled`, as a fallback when real-time updates are unavailable. */
export function usePolling(tick: () => void, intervalMs: number, enabled = true) {
  const callback = useRef(tick)
  useEffect(() => {
    callback.current = tick
  })
  useEffect(() => {
    if (!enabled) return
    const id = window.setInterval(() => callback.current(), intervalMs)
    return () => window.clearInterval(id)
  }, [intervalMs, enabled])
}
