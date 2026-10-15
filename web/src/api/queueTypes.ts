import type { Gender } from './types'

export type Relationship = 'Self' | 'Son' | 'Daughter' | 'Wife' | 'Husband' | 'Mother' | 'Father' | 'Other'
export type PersonKind = 'Self' | 'FamilyProfile' | 'LinkedAccount'
export type InvitationStatus = 'Pending' | 'Active' | 'Declined' | 'Revoked'
export type SessionState = 'Scheduled' | 'Open' | 'Paused' | 'Closed' | 'Cancelled'
export type AppointmentStatus =
  | 'Booked'
  | 'CheckedIn'
  | 'Called'
  | 'InConsultation'
  | 'Completed'
  | 'Missed'
  | 'Requeued'
  | 'NoShow'
  | 'Cancelled'
export type BookingSource = 'Online' | 'WalkIn' | 'StaffBooked'
export type EmergencyReason =
  | 'AcutePain'
  | 'BreathingDifficulty'
  | 'Injury'
  | 'ChildWithHighFever'
  | 'DoctorsDiscretion'
  | 'Other'
export type AnnouncementType =
  | 'OutOfClinic'
  | 'Leave'
  | 'Holiday'
  | 'ClinicClosure'
  | 'DelayedOpening'
  | 'ChangedHours'
  | 'General'
export type DayName = 'Sunday' | 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday'

export interface Notice {
  type: string
  message: string
  startsAt: string
  endsAt: string | null
}

export interface SessionAvailability {
  name: string
  startsAt: string
  endsAt: string
  state: string
  capacity: number
  booked: number
  remaining: number
  bookable: boolean
  reason: string | null
  nowServing: string | null
  waiting: number | null
}

export interface Availability {
  doctorCode: string
  acceptingBookings: boolean
  days: { date: string; sessions: SessionAvailability[] }[]
  notices: Notice[]
}

export interface PersonDto {
  patientId: string
  fullName: string
  relationship: Relationship
  kind: PersonKind
  dateOfBirth: string | null
  gender: Gender | null
  isMinor: boolean
  historyAccess: 'Full' | 'BookedByMe'
  linkId: string | null
}

export interface LiveStatus {
  appointmentId: string
  reference: string
  token: string
  status: AppointmentStatus
  patientId: string
  patientName: string
  doctorName: string
  doctorCode: string | null
  clinicName: string
  clinicAddress: string
  sessionId: string
  date: string
  sessionName: string
  sessionStartsAt: string
  sessionEndsAt: string
  sessionState: SessionState
  patientsAhead: number | null
  nowServing: string | null
  estimateMinMinutes: number | null
  estimateMaxMinutes: number | null
  requeueAfterCompletions: number | null
  canCancel: boolean
  source: BookingSource
  followUpNote: string | null
  notices: Notice[]
  asOf: string
}

export interface Visit {
  appointmentId: string
  reference: string
  token: string
  status: AppointmentStatus
  date: string
  sessionName: string
  patientId: string
  patientName: string
  doctorName: string
  doctorCode: string | null
  clinicName: string
  source: BookingSource
  bookedByMe: boolean
  followUpNote: string | null
}

export interface LinkDto {
  linkId: string
  status: InvitationStatus
  relationship: Relationship
  otherPartyName: string
  inviteeMobileMasked: string | null
  createdAt: string
  respondedAt: string | null
}

export interface LinksResponse {
  sent: LinkDto[]
  received: LinkDto[]
  sharingMyHistory: LinkDto[]
}

export interface NotificationItem {
  id: number
  eventType: string
  title: string
  body: string
  appointmentId: string | null
  createdAt: string
  isRead: boolean
}

export interface NotificationList {
  items: NotificationItem[]
  unread: number
  page: number
  total: number
}

export interface ClinicContextDto {
  doctorId: string
  doctorName: string
  doctorCode: string | null
  clinicId: string
  clinicName: string
  role: 'Doctor' | 'Receptionist'
}

export interface SessionSummary {
  sessionId: string
  date: string
  name: string
  startsAt: string
  endsAt: string
  state: SessionState
  capacity: number
  booked: number
  waiting: number
  completed: number
  missed: number
  nowServing: string | null
  delayMinutes: number
}

export interface QueueEntry {
  appointmentId: string
  reference: string
  token: string
  status: AppointmentStatus
  patientId: string
  patientName: string
  patientAge: number | null
  source: BookingSource
  bookedByName: string | null
  bookedAt: string
  checkedInAt: string | null
  calledAt: string | null
  position: number | null
  priority: 'Normal' | 'Emergency'
  emergencyReason: EmergencyReason | null
  emergencyNote: string | null
  missCount: number
  requeueAfterCompletions: number | null
  missAllowedAt: string | null
  completedAt: string | null
  cancellationReason: string | null
}

export interface QueueView {
  session: SessionSummary
  nowServing: QueueEntry | null
  waiting: QueueEntry[]
  missed: QueueEntry[]
  done: QueueEntry[]
  avgConsultMinutes: number
  gracePeriodMinutes: number
  requeueAfterCompletions: number
}

export interface PatientMatch {
  patientId: string
  fullName: string
  relationship: Relationship
  ageYears: number | null
}

export interface SessionTemplate {
  dayOfWeek: DayName
  name: string
  startTime: string
  endTime: string
  capacity: number
}

export interface QueueSettings {
  bookingWindowDays: number
  avgConsultMinutes: number
  gracePeriodMinutes: number
  requeueAfterCompletions: number
  secondMissRule: 'NoShow' | 'RequeueAgain'
  turnApproachingThreshold: number
}

export interface ScheduleExceptionDto {
  id: number
  kind: 'Closed' | 'SpecialHours'
  startDate: string
  endDate: string
  sessionName: string | null
  startTime: string | null
  endTime: string | null
  capacity: number | null
  reason: string | null
  announcementId: string | null
}

export interface ScheduleResponse {
  settings: QueueSettings
  sessions: SessionTemplate[]
  exceptions: ScheduleExceptionDto[]
}

export interface AnnouncementDto {
  id: string
  type: AnnouncementType
  message: string
  startsAt: string
  endsAt: string | null
  delayMinutes: number | null
  isCurrent: boolean
  createdAt: string
}

export interface HistoryEvent {
  type: string
  from: AppointmentStatus | null
  to: AppointmentStatus | null
  reason: string | null
  occurredAt: string
}

export interface ClinicVisit {
  appointmentId: string
  reference: string
  token: string
  status: AppointmentStatus
  date: string
  sessionName: string
  patientName: string
  source: BookingSource
  priority: 'Normal' | 'Emergency'
  emergencyReason: EmergencyReason | null
  missCount: number
  bookedAt: string
  completedAt: string | null
  cancellationReason: string | null
  events: HistoryEvent[]
}

export interface StaffResponse {
  members: { assignmentId: number; userId: string; fullName: string; mobileMasked: string; grantedAt: string }[]
  pending: { invitationId: string; fullName: string; mobileMasked: string; status: InvitationStatus; createdAt: string }[]
}

export interface StaffInvitationReceived {
  invitationId: string
  doctorName: string
  doctorCode: string | null
  clinicName: string
  createdAt: string
}

export interface MfaChallenge {
  mfaRequired: true
  mfaTicket: string
  enrollment: { secret: string; otpAuthUri: string } | null
}

export interface AdminOverview {
  pendingDoctors: number
  verifiedDoctors: number
  openQueuesToday: number
  appointmentsToday: number
  notificationFailures24h: number
  outboxBacklog: number
}

export interface AdminPatientRow {
  patientId: string
  fullName: string
  kind: string
  relationship: Relationship
  accountMobileMasked: string | null
  appointments: number
  createdAt: string
}

export interface AdminAppointmentRow {
  appointmentId: string
  reference: string
  token: string
  status: AppointmentStatus
  date: string
  sessionName: string
  patientName: string
  doctorName: string
  doctorCode: string | null
  source: BookingSource
  priority: 'Normal' | 'Emergency'
  emergencyReason: EmergencyReason | null
  missCount: number
  cancellationReason: string | null
  bookedAt: string
  events: (HistoryEvent & { actorUserId: string | null })[]
}

export interface AdminPatientHistory {
  patientId: string
  fullName: string
  kind: string
  dateOfBirth: string | null
  accountMobileMasked: string | null
  createdAt: string
  appointments: AdminAppointmentRow[]
}

export interface AdminQueueRow {
  sessionId: string
  doctorName: string
  doctorCode: string | null
  date: string
  name: string
  state: SessionState
  booked: number
  waiting: number
  completed: number
  missed: number
  nowServing: string | null
}

export interface FailureRow {
  id: number
  eventType: string
  channel: 'InApp' | 'WebPush' | 'Sms'
  status: string
  error: string | null
  userId: string | null
  appointmentId: string | null
  createdAt: string
}

export interface AuditRow {
  id: number
  actorUserId: string | null
  action: string
  entityType: string
  entityId: string
  purpose: string | null
  details: string | null
  occurredAt: string
}

export const appointmentStatusLabels: Record<AppointmentStatus, string> = {
  Booked: 'Booked',
  CheckedIn: 'Checked in',
  Called: 'Called',
  InConsultation: 'In consultation',
  Completed: 'Completed',
  Missed: 'Missed turn',
  Requeued: 'Back in the list',
  NoShow: 'No-show',
  Cancelled: 'Cancelled',
}

export const relationshipLabels: Record<Relationship, string> = {
  Self: 'Myself',
  Son: 'Son',
  Daughter: 'Daughter',
  Wife: 'Wife',
  Husband: 'Husband',
  Mother: 'Mother',
  Father: 'Father',
  Other: 'Other family member',
}

export const emergencyLabels: Record<EmergencyReason, string> = {
  AcutePain: 'Acute pain',
  BreathingDifficulty: 'Breathing difficulty',
  Injury: 'Injury',
  ChildWithHighFever: 'Child with high fever',
  DoctorsDiscretion: "Doctor's discretion",
  Other: 'Other',
}

export const announcementLabels: Record<AnnouncementType, string> = {
  OutOfClinic: 'Out of clinic',
  Leave: 'Leave',
  Holiday: 'Holiday',
  ClinicClosure: 'Clinic closure',
  DelayedOpening: 'Delayed opening',
  ChangedHours: 'Changed working hours',
  General: 'General notice',
}

export const sourceLabels: Record<BookingSource, string> = {
  Online: 'Online',
  WalkIn: 'Walk-in',
  StaffBooked: 'Booked by clinic',
}

export const dayNames: DayName[] = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']
