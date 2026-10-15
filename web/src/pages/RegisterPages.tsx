import { Link, Navigate, useNavigate } from 'react-router-dom'
import { useAuth } from '../auth/authState'
import { landingPath } from '../auth/landing'
import { PhoneAuthFlow } from './SignInPage'

/** Entry point for new users: choose between a patient account and doctor registration. */
export function RegisterChoicePage() {
  const { token } = useAuth()
  if (token) return <Navigate to="/" replace />

  return (
    <section className="panel narrow">
      <title>Register · Smart Doctor Queue</title>
      <h1>Create an account</h1>
      <p className="lead">Choose how you will use Smart Doctor Queue.</p>
      <ul className="role-choice-list">
        <li>
          <Link to="/register/patient" className="role-choice">
            <span className="role-choice-title">I'm a patient</span>
            <span className="role-choice-text">Book tokens for yourself and your family, and follow the queue live.</span>
          </Link>
        </li>
        <li>
          <Link to="/register/doctor" className="role-choice">
            <span className="role-choice-title">I'm a doctor</span>
            <span className="role-choice-text">
              Register with your Medical Registration ID. After verification you get a Doctor ID and your clinic queue.
            </span>
          </Link>
        </li>
      </ul>
      <p className="meta">Already registered? <Link to="/sign-in">Sign in</Link></p>
      <p className="meta">Clinic staff: ask your doctor to invite your mobile number, then sign in.</p>
    </section>
  )
}

export function PatientRegisterPage() {
  const navigate = useNavigate()
  return (
    <section className="panel narrow">
      <title>Patient registration · Smart Doctor Queue</title>
      <PhoneAuthFlow mode="patient" onDone={async (token) => navigate(await landingPath(token), { replace: true })} />
      <p className="meta">Are you a doctor? <Link to="/register/doctor">Register as a doctor</Link></p>
    </section>
  )
}

/** Step 1 of doctor registration; step 2 is the professional-details form at /doctor/register. */
export function DoctorRegisterPage() {
  const { token } = useAuth()
  const navigate = useNavigate()
  if (token) return <Navigate to="/doctor/register" replace />

  return (
    <section className="panel narrow">
      <title>Doctor registration · Smart Doctor Queue</title>
      <PhoneAuthFlow mode="doctor" onDone={(t) => navigate(t.roles.includes('SuperAdmin') ? '/admin' : '/doctor/register', { replace: true })} />
      <p className="meta">Booking as a patient? <Link to="/register/patient">Create a patient account</Link></p>
    </section>
  )
}
