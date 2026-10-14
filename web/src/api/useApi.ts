import { useCallback, useEffect, useState } from 'react'
import { api } from './client'

interface State<T> {
  key: string | null
  path: string | null
  data?: T
  error?: unknown
}

/**
 * Loads `path` and reloads when it changes. Pass null to skip loading.
 * While reloading the same path, the previous data stays visible.
 */
export function useApi<T>(path: string | null) {
  const [version, setVersion] = useState(0)
  const key = path === null ? null : `${version}:${path}`
  const [state, setState] = useState<State<T>>({ key: null, path: null })

  useEffect(() => {
    if (key === null || path === null) return
    const controller = new AbortController()
    api<T>(path, { signal: controller.signal })
      .then((data) => setState({ key, path, data }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ key, path, error })
      })
    return () => controller.abort()
  }, [key, path])

  const reload = useCallback(() => setVersion((v) => v + 1), [])
  const isCurrent = state.key === key
  return {
    data: state.path === path ? state.data : undefined,
    error: isCurrent ? state.error : undefined,
    loading: key !== null && !isCurrent,
    reload,
  }
}
