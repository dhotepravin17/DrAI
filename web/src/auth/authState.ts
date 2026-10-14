import { createContext, use } from 'react'
import type { Me } from '../api/types'

export interface AuthState {
  token: string | null
  me: Me | null
  loading: boolean
  signIn: (token: string) => void
  signOut: () => void
  refreshMe: () => Promise<void>
}

export const AuthContext = createContext<AuthState | null>(null)

export function useAuth(): AuthState {
  const context = use(AuthContext)
  if (!context) throw new Error('useAuth must be used inside AuthProvider')
  return context
}
