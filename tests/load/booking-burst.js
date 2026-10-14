// k6 load test for the pilot targets in PRD §35.
//
//   k6 run tests/load/booking-burst.js                                  # against http://localhost:5221
//   k6 run -e BASE_URL=https://staging.example.in -e DOCTOR_CODE=NGP-Dr-0001 -e SESSION_NAME=Evening tests/load/booking-burst.js
//
// Scenarios:
//   queue_reads   – patients polling their live queue status (p95 < 500 ms)
//   booking_burst – 20 bookings per second on one queue (p95 < 1 s, zero duplicate tokens)
//
// Sign-in uses the development OTP shortcut (Otp:ExposeCodeInResponse), so run it against a Development or
// staging build with that setting, never production. Each virtual user creates its own patient account on a
// 99xxxxxxxx test number. Point it at a doctor whose sessions have enough capacity for the burst.
import http from 'k6/http'
import { check, fail } from 'k6'
import { Counter } from 'k6/metrics'

const BASE = (__ENV.BASE_URL || 'http://localhost:5221') + '/api/v1'
const DOCTOR = __ENV.DOCTOR_CODE
const JSON_HEADERS = { 'Content-Type': 'application/json' }
const SESSION_NAME = __ENV.SESSION_NAME || 'Morning'
// Today in IST (PRD §6.1) unless SESSION_DATE is given.
const SESSION_DATE = __ENV.SESSION_DATE || new Date(Date.now() + 330 * 60000).toISOString().slice(0, 10)

const duplicateTokens = new Counter('duplicate_tokens')

export const options = {
  scenarios: {
    queue_reads: {
      executor: 'constant-vus', exec: 'queueReads', vus: Number(__ENV.READERS || 200), duration: __ENV.DURATION || '2m',
    },
    booking_burst: {
      executor: 'constant-arrival-rate', exec: 'bookingBurst', rate: 20, timeUnit: '1s', duration: '30s',
      preAllocatedVUs: 40, maxVUs: 100, startTime: '30s',
    },
  },
  thresholds: {
    'http_req_duration{name:queue_status}': ['p(95)<500'],
    'http_req_duration{name:book}': ['p(95)<1000'],
    'http_req_failed{name:book}': ['rate<0.01'],
    duplicate_tokens: ['count==0'],
  },
}

function mobileFor(vu, iteration) {
  return '99' + String((vu * 100000 + iteration) % 100000000).padStart(8, '0')
}

function signUp(mobile) {
  const request = http.post(`${BASE}/auth/otp/request`, JSON.stringify({ mobileNumber: mobile }), { headers: JSON_HEADERS })
  if (request.status !== 200) fail(`OTP request failed: ${request.status}`)
  const code = request.json('developmentCode')
  if (!code) fail('The API did not return the OTP. Enable Otp:ExposeCodeInResponse in a Development build.')
  const verify = http.post(`${BASE}/auth/otp/verify`, JSON.stringify({ mobileNumber: mobile, code }), { headers: JSON_HEADERS })
  const reference = http.get(`${BASE}/reference`).json()
  const account = http.post(`${BASE}/auth/accounts`, JSON.stringify({
    phoneTicket: verify.json('phoneTicket'),
    fullName: `Load Test ${mobile.slice(-4)}`,
    privacyNoticeVersion: reference.legalVersions.privacyNotice,
    termsVersion: reference.legalVersions.terms,
  }), { headers: JSON_HEADERS })
  if (account.status !== 200) fail(`Account creation failed: ${account.status} ${account.body}`)
  return { Authorization: `Bearer ${account.json('accessToken')}`, 'Content-Type': 'application/json' }
}

export function setup() {
  if (!DOCTOR) fail('Set DOCTOR_CODE to a verified doctor with open capacity, e.g. -e DOCTOR_CODE=NGP-Dr-0001')
  const profile = http.get(`${BASE}/doctors/${DOCTOR}`)
  if (profile.status !== 200) fail(`Doctor ${DOCTOR} not found (${profile.status}).`)
  return { doctor: DOCTOR }
}

const sessions = {}

export function queueReads(data) {
  const vu = __VU
  if (!sessions[vu]) {
    const headers = signUp(mobileFor(vu, 0))
    const patientId = http.get(`${BASE}/me`, { headers }).json('patient.patientId')
    const booking = http.post(`${BASE}/appointments`, JSON.stringify({ doctorCode: data.doctor, date: SESSION_DATE, sessionName: SESSION_NAME, patientId }),
      { headers: { ...headers, 'Idempotency-Key': `load-${vu}` } })
    sessions[vu] = { headers, appointmentId: booking.json('appointmentId') }
  }
  const { headers, appointmentId } = sessions[vu]
  if (!appointmentId) return
  const status = http.get(`${BASE}/appointments/${appointmentId}`, { headers, tags: { name: 'queue_status' } })
  check(status, { 'status 200': (r) => r.status === 200 })
}

const tokensSeen = new Set()

export function bookingBurst(data) {
  const headers = signUp(mobileFor(50000 + __VU, __ITER + 1))
  const patientId = http.get(`${BASE}/me`, { headers }).json('patient.patientId')
  const response = http.post(`${BASE}/appointments`, JSON.stringify({ doctorCode: data.doctor, date: SESSION_DATE, sessionName: SESSION_NAME, patientId }),
    { headers: { ...headers, 'Idempotency-Key': `burst-${__VU}-${__ITER}` }, tags: { name: 'book' } })
  check(response, { 'booked': (r) => r.status === 200 || r.status === 201 })
  const key = `${response.json('sessionId')}:${response.json('token')}`
  // Each VU only sees its own bookings, so this catches duplicates within a VU; the SQL check in
  // docs/operations/load-testing.md verifies the whole run.
  if (tokensSeen.has(key)) duplicateTokens.add(1)
  tokensSeen.add(key)
}
