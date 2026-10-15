import { useMemo, useState } from 'react'
import { NavLink, Outlet } from 'react-router-dom'
import { errorMessage } from '../api/client'
import type { ClinicContextDto } from '../api/queueTypes'
import { useApi } from '../api/useApi'
import { useAuth } from '../auth/authState'
import { Loading } from '../components/Layout'
import { ClinicScopeContext } from './clinicScopeState'

const STORAGE_KEY = 'smartqueue.clinicDoctor'

function readSelection(): string | null {
  try {
    return localStorage.getItem(STORAGE_KEY)
  } catch {
    return null
  }
}

export function ClinicLayout() {
  const { me } = useAuth()
  const { data: contexts, error, loading } = useApi<ClinicContextDto[]>('/clinic/contexts')
  const [selectedId, setSelectedId] = useState<string | null>(readSelection)

  const current = contexts?.find((c) => c.doctorId === selectedId) ?? contexts?.[0]
  const scope = useMemo(() => {
    if (!contexts || !current) return null
    return {
      contexts,
      current,
      select: (doctorId: string) => {
        setSelectedId(doctorId)
        try {
          localStorage.setItem(STORAGE_KEY, doctorId)
        } catch {
          // Selection then lasts for this visit only.
        }
      },
      path: (path: string) => `${path}${path.includes('?') ? '&' : '?'}doctorId=${current.doctorId}`,
    }
  }, [contexts, current])

  if (loading && !contexts) return <Loading />
  if (error) return <p className="alert alert-error" role="alert">{errorMessage(error)}</p>
  if (!scope) {
    return (
      <section className="panel narrow">
        <title>Clinic · MediCare Plus</title>
        <h1>No clinic yet</h1>
        {me?.doctor ? (
          <p>Your doctor registration is {me.doctor.status === 'Verified' ? 'verified' : 'not verified yet'}. The clinic tools open once you are verified.</p>
        ) : (
          <p>Clinic tools are for verified doctors and the staff they invite. If you work at a clinic, ask the doctor to invite your mobile number.</p>
        )}
      </section>
    )
  }

  const isDoctor = scope.current.role === 'Doctor'
  return (
    <ClinicScopeContext value={scope}>
      <div className="clinic-head">
        <div>
          <p className="meta">{scope.current.clinicName} · {scope.current.role === 'Doctor' ? 'Your clinic' : `Staff for ${scope.current.doctorName}`}</p>
          {scope.contexts.length > 1 && (
            <label className="inline-field">
              <span>Clinic</span>
              <select value={scope.current.doctorId} onChange={(e) => scope.select(e.target.value)}>
                {scope.contexts.map((c) => <option key={c.doctorId} value={c.doctorId}>{c.doctorName} — {c.clinicName}</option>)}
              </select>
            </label>
          )}
        </div>
        <nav aria-label="Clinic">
          <ul className="tabs">
            <li><NavLink className="tab" to="/clinic" end>Appointments</NavLink></li>
            <li><NavLink className="tab" to="/clinic/schedule">Schedule</NavLink></li>
            <li><NavLink className="tab" to="/clinic/announcements">Notices &amp; leave</NavLink></li>
            <li><NavLink className="tab" to="/clinic/history">History</NavLink></li>
            <li><NavLink className="tab" to="/clinic/reports">Reports</NavLink></li>
            {isDoctor && <li><NavLink className="tab" to="/clinic/staff">Staff</NavLink></li>}
            {isDoctor && <li><NavLink className="tab" to="/clinic/profile">Profile &amp; QR</NavLink></li>}
          </ul>
        </nav>
      </div>
      <Outlet />
    </ClinicScopeContext>
  )
}
