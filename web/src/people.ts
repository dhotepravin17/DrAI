import { relationshipLabels, type PersonDto } from './api/queueTypes'

export function personLabel(p: PersonDto) {
  return p.kind === 'Self' ? `${p.fullName} (myself)` : `${p.fullName} — ${relationshipLabels[p.relationship]}`
}

export function familyMemberBody(data: FormData) {
  const text = (name: string) => String(data.get(name) ?? '').trim() || null
  return {
    fullName: text('fullName'),
    relationship: text('relationship'),
    dateOfBirth: text('dateOfBirth'),
    gender: text('gender'),
    guardianConsent: data.get('guardianConsent') === 'on',
  }
}

export function isMinor(dateOfBirth: string | null | undefined): boolean {
  if (!dateOfBirth) return false
  const dob = new Date(`${dateOfBirth}T00:00:00`)
  const eighteenth = new Date(dob)
  eighteenth.setFullYear(dob.getFullYear() + 18)
  return eighteenth > new Date()
}
