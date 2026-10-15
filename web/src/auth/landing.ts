import { api } from '../api/client'
import type { Me, TokenResponse } from '../api/types'

/**
 * Where each role starts after signing in or registering. The Doctor role is granted when a doctor first saves their
 * registration, so verification status (not the role) decides between the clinic and the registration page.
 * Call after `signIn`, so the request carries the new token.
 */
export async function landingPath(token: TokenResponse): Promise<string> {
  if (token.roles.includes('SuperAdmin')) return '/admin'
  try {
    const me = await api<Me>('/me')
    if (me.doctor) return me.doctor.status === 'Verified' ? '/clinic' : '/doctor/register'
    if (me.roles.includes('Receptionist')) return '/clinic'
  } catch {
    // Fall back to the patient area; the page itself reports any problem.
  }
  return '/appointments'
}
