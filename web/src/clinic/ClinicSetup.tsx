import { Link } from 'react-router-dom'
import type { ScheduleResponse } from '../api/queueTypes'
import { useApi } from '../api/useApi'
import { useClinicScope } from './clinicScopeState'

/**
 * Shown to a doctor until their weekly clinic hours are set. Without hours, patients see "This doctor has not
 * published clinic hours yet" and cannot join the queue (PRD flow: verification → clinic schedule setup).
 */
export function ClinicSetupChecklist() {
  const scope = useClinicScope()
  const isDoctor = scope.current.role === 'Doctor'
  const schedule = useApi<ScheduleResponse>(isDoctor ? scope.path('/clinic/schedule') : null)
  if (!isDoctor || !schedule.data || schedule.data.sessions.length > 0) return null

  return (
    <div className="panel alert-info" role="note">
      <h2>Finish setting up your clinic</h2>
      <p>Patients cannot join your queue until you publish your clinic hours.</p>
      <ol className="setup-list">
        <li>
          <strong><Link to="/clinic/schedule">Set your weekly clinic hours</Link></strong> — days, morning and evening
          sessions, and places per session. Holidays and special hours for a date go on the same page.
        </li>
        <li>
          <Link to="/clinic/announcements">Post out-of-clinic, leave or delay notices</Link> when you are away. Patients see
          them on your profile, and affected bookings are told.
        </li>
        <li><Link to="/clinic/profile">Print your QR code</Link> for the front desk so patients can join the queue.</li>
        <li><Link to="/clinic/staff">Invite your receptionist</Link> to run the queue (optional).</li>
      </ol>
    </div>
  )
}
