import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { api, ApiError, AUTH_EXPIRED_EVENT, setApiToken } from '../api/client'
import type { Me } from '../api/types'
import { AuthContext } from './authState'

const STORAGE_KEY = 'smartqueue.accessToken'

function readStoredToken(): string | null {
  try {
    return localStorage.getItem(STORAGE_KEY)
  } catch {
    return null
  }
}

function storeToken(token: string | null) {
  try {
    if (token) localStorage.setItem(STORAGE_KEY, token)
    else localStorage.removeItem(STORAGE_KEY)
  } catch {
    // Storage can be unavailable (private mode); the session then lasts for this tab only.
  }
}

const initialToken = readStoredToken()
setApiToken(initialToken)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [token, setToken] = useState<string | null>(initialToken)
  const [me, setMe] = useState<Me | null>(null)
  const [loading, setLoading] = useState(initialToken !== null)

  const signIn = useCallback((next: string) => {
    setApiToken(next)
    storeToken(next)
    setMe(null)
    setLoading(true)
    setToken(next)
  }, [])

  const signOut = useCallback(() => {
    setApiToken(null)
    storeToken(null)
    setMe(null)
    setLoading(false)
    setToken(null)
  }, [])

  const refreshMe = useCallback(async () => {
    try {
      setMe(await api<Me>('/me'))
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) signOut()
    }
  }, [signOut])

  useEffect(() => {
    if (!token) return
    const controller = new AbortController()
    api<Me>('/me', { signal: controller.signal })
      .then(setMe)
      .catch((error: unknown) => {
        if (error instanceof ApiError && error.status === 401) signOut()
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })
    return () => controller.abort()
  }, [token, signOut])

  useEffect(() => {
    window.addEventListener(AUTH_EXPIRED_EVENT, signOut)
    return () => window.removeEventListener(AUTH_EXPIRED_EVENT, signOut)
  }, [signOut])

  const value = useMemo(
    () => ({ token, me, loading, signIn, signOut, refreshMe }),
    [token, me, loading, signIn, signOut, refreshMe],
  )
  return <AuthContext value={value}>{children}</AuthContext>
}

