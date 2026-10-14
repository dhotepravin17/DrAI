export type DoctorStatus =
  | 'Draft'
  | 'PendingVerification'
  | 'Verified'
  | 'Rejected'
  | 'Suspended'
  | 'Deactivated'

export type Role = 'SuperAdmin' | 'Doctor' | 'Receptionist'

export type Gender = 'Female' | 'Male' | 'Other' | 'PreferNotToSay'

export type RejectionReason =
  | 'InvalidRegistrationNumber'
  | 'DetailsMismatch'
  | 'DuplicateRegistration'
  | 'InsufficientEvidence'
  | 'Other'

export interface City {
  code: string
  name: string
  state: string
}

export interface ReferenceData {
  cities: City[]
  medicalAuthorities: string[]
  specialties: string[]
  legalVersions: { privacyNotice: string; terms: string; doctorDeclaration: string }
}

export interface Declaration {
  version: string
  text: string
}

export interface OtpRequestResponse {
  expiresAt: string
  developmentCode: string | null
}

export interface AccountSummary {
  userId: string
  fullName: string
  roles: Role[]
}

export interface OtpVerifyResponse {
  phoneTicket: string
  accounts: AccountSummary[]
}

export interface TokenResponse {
  accessToken: string
  expiresAt: string
  userId: string
  roles: Role[]
}

export interface Me {
  userId: string
  fullName: string
  mobileNumberMasked: string
  email: string | null
  roles: Role[]
  patient: { patientId: string; fullName: string; dateOfBirth: string | null; gender: Gender | null; city: string | null } | null
  doctor: { doctorId: string; status: DoctorStatus; doctorCode: string | null } | null
}

export interface DoctorCard {
  doctorCode: string
  fullName: string
  specialty: string
  qualifications: string | null
  cityCode: string
  cityName: string
  clinic: { name: string; address: string; publicPhone: string | null } | null
  isVerified: boolean
  acceptingBookings: boolean
}

export interface Paged<T> {
  items: T[]
  page: number
  pageSize: number
  total: number
}

export interface Registration {
  doctorId: string
  status: DoctorStatus
  doctorCode: string | null
  fullName: string
  email: string | null
  specialty: string
  qualifications: string | null
  medicalRegistrationId: string
  medicalRegistrationAuthority: string
  primaryCityCode: string
  clinicName: string | null
  clinicAddress: string | null
  clinicPublicPhone: string | null
  submissionReference: string | null
  submittedAt: string | null
  rejectionReason: RejectionReason | null
  statusNote: string | null
  canAcceptBookings: boolean
}

export interface AdminDoctorRow {
  doctorId: string
  fullName: string
  specialty: string
  primaryCityCode: string
  status: DoctorStatus
  doctorCode: string | null
  submittedAt: string | null
  possibleDuplicate: boolean
}

export interface AdminDoctorDetail {
  doctorId: string
  userId: string
  fullName: string
  email: string | null
  mobileNumberMasked: string
  specialty: string
  qualifications: string | null
  medicalRegistrationId: string
  medicalRegistrationAuthority: string
  primaryCityCode: string
  status: DoctorStatus
  doctorCode: string | null
  clinicName: string | null
  clinicAddress: string | null
  clinicPublicPhone: string | null
  submissionReference: string | null
  submittedAt: string | null
  verifiedAt: string | null
  verificationReference: string | null
  rejectionReason: RejectionReason | null
  statusNote: string | null
  possibleDuplicate: boolean
  declarations: { version: string; accepted: boolean; acceptedAt: string; submissionReference: string; ipAddress: string | null }[]
  sameRegistrationNumber: { doctorId: string; fullName: string; status: DoctorStatus; doctorCode: string | null }[]
  rowVersion: string
  plan: DoctorPlan
}

export type DoctorPlan = 'Free' | 'Pro' | 'Premium'

export const planLabels: Record<DoctorPlan, string> = {
  Free: 'Free — 1 staff account, 90 days of history',
  Pro: 'Pro — 5 staff accounts, full history, reports',
  Premium: 'Premium — organization-wide',
}

export interface AdminCityRow {
  code: string
  name: string
  state: string
  isActive: boolean
  issuedDoctorIds: number
}

export const statusLabels: Record<DoctorStatus, string> = {
  Draft: 'Draft',
  PendingVerification: 'Pending verification',
  Verified: 'Verified',
  Rejected: 'Rejected',
  Suspended: 'Suspended',
  Deactivated: 'Deactivated',
}

export const rejectionLabels: Record<RejectionReason, string> = {
  InvalidRegistrationNumber: 'Registration number not found',
  DetailsMismatch: 'Details do not match the register',
  DuplicateRegistration: 'Registration number already in use',
  InsufficientEvidence: 'Not enough evidence to verify',
  Other: 'Other',
}

export interface EventCount { name: string; dimension: string | null; count: number; average: number | null }

export interface PlatformAnalytics {
  from: string
  to: string
  metrics: {
    bookings: number
    onlineShare: number | null
    forSelfShare: number | null
    cancellationRate: number | null
    noShowRate: number | null
    averageConsultMinutes: number | null
    averageWaitMinutes: number | null
    notificationFailureRate: number | null
    averageReviewHours: number | null
    patientsRegistered: number
    familyMembersAdded: number
    searches: number
    zeroResultSearchShare: number | null
  }
  events: EventCount[]
}

export interface ClinicReport {
  from: string
  to: string
  appointments: number
  completed: number
  cancelled: number
  noShows: number
  missed: number
  emergencies: number
  bySource: Record<string, number>
  averageConsultMinutes: number | null
  averageWaitMinutes: number | null
  daily: { date: string; appointments: number; completed: number; noShows: number }[]
}
