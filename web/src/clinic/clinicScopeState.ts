import { createContext, use } from 'react'
import type { ClinicContextDto } from '../api/queueTypes'

export interface ClinicScopeState {
  contexts: ClinicContextDto[]
  current: ClinicContextDto
  select: (doctorId: string) => void
  /** Adds the selected doctor to a clinic API path. */
  path: (path: string) => string
}

export const ClinicScopeContext = createContext<ClinicScopeState | null>(null)

export function useClinicScope(): ClinicScopeState {
  const scope = use(ClinicScopeContext)
  if (!scope) throw new Error('useClinicScope must be used inside ClinicLayout')
  return scope
}
