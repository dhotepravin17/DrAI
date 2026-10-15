import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { api, ApiError, errorMessage, loadReference } from '../api/client'
import type { AccountSummary, OtpRequestResponse, OtpVerifyResponse, ReferenceData, TokenResponse } from '../api/types'
import type { MfaChallenge } from '../api/queueTypes'
import { useAuth } from '../auth/authState'
import { landingPath } from '../auth/landing'
import { FormAlert, TextField } from '../components/Fields'
import { clearFieldErrorOnInput } from '../format'

type Step =
  | { kind: 'mobile' }
  | { kind: 'code'; mobile: string; developmentCode: string | null }
  | { kind: 'accounts'; ticket: string; accounts: AccountSummary[] }
  | { kind: 'create'; ticket: string; canGoBack: boolean; accounts: AccountSummary[] }
  | { kind: 'mfa'; challenge: MfaChallenge }

/** Sign-in, patient registration and step 1 of doctor registration share the phone and code steps. */
export type PhoneAuthMode = 'signin' | 'patient' | 'doctor'

function safeNext(value: string | null): string | null {
  return value && value.startsWith('/') && !value.startsWith('//') ? value : null
}

export function SignInPage() {
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const next = safeNext(params.get('next'))

  return (
    <section className="panel narrow">
      <title>Sign in · Smart Doctor Queue</title>
      <PhoneAuthFlow mode="signin" onDone={async (token) => navigate(next ?? await landingPath(token), { replace: true })} />
      <p className="meta">New here? <Link to="/register">Register as a patient or a doctor</Link></p>
    </section>
  )
}

/** Runs the phone, code, account and authenticator steps, signs the person in, then calls `onDone`. */
export function PhoneAuthFlow({
  mode, onDone,
}: {
  mode: PhoneAuthMode
  onDone: (token: TokenResponse) => void | Promise<void>
}) {
  const [step, setStep] = useState<Step>({ kind: 'mobile' })
  const { signIn } = useAuth()

  const finish = (token: TokenResponse) => {
    signIn(token.accessToken)
    void onDone(token)
  }

  return (
    <>
      {step.kind === 'mobile' && (
        <MobileStep mode={mode} onSent={(mobile, developmentCode) => setStep({ kind: 'code', mobile, developmentCode })} />
      )}
      {step.kind === 'code' && (
        <CodeStep
          mobile={step.mobile}
          developmentCode={step.developmentCode}
          onChangeNumber={() => setStep({ kind: 'mobile' })}
          onVerified={(result) =>
            setStep(result.accounts.length === 0
              ? { kind: 'create', ticket: result.phoneTicket, canGoBack: false, accounts: [] }
              : { kind: 'accounts', ticket: result.phoneTicket, accounts: result.accounts })}
        />
      )}
      {step.kind === 'accounts' && (
        <AccountStep
          mode={mode}
          ticket={step.ticket}
          accounts={step.accounts}
          onCreate={() => setStep({ kind: 'create', ticket: step.ticket, canGoBack: true, accounts: step.accounts })}
          onSignedIn={finish}
          onMfa={(challenge) => setStep({ kind: 'mfa', challenge })}
        />
      )}
      {step.kind === 'mfa' && (
        <MfaStep challenge={step.challenge} onSignedIn={finish} onRestart={() => setStep({ kind: 'mobile' })} />
      )}
      {step.kind === 'create' && (
        <CreateAccountStep
          mode={mode}
          ticket={step.ticket}
          onBack={step.canGoBack ? () => setStep({ kind: 'accounts', ticket: step.ticket, accounts: step.accounts }) : undefined}
          onCreated={finish}
        />
      )}
    </>
  )
}

const copy: Record<PhoneAuthMode, { step: string | null; mobileTitle: string; mobileLead: string; accountsTitle: string; createTitle: string; createLead: string }> = {
  signin: {
    step: null,
    mobileTitle: 'Sign in or create an account',
    mobileLead: 'We will send a 6-digit code to your mobile number.',
    accountsTitle: 'Choose an account',
    createTitle: 'Create your account',
    createLead: 'Only your name is required. You can add family members later.',
  },
  patient: {
    step: null,
    mobileTitle: 'Create your patient account',
    mobileLead: 'Book tokens for yourself and your family and follow your turn live. We will send a 6-digit code to your mobile number.',
    accountsTitle: 'You already have an account on this number',
    createTitle: 'Your details',
    createLead: 'Only your name is required. You can add family members later.',
  },
  doctor: {
    step: 'Step 1 of 2 · Your account',
    mobileTitle: 'Register as a doctor',
    mobileLead: 'First create your account with your mobile number. Your professional details and Medical Registration ID come next.',
    accountsTitle: 'You already have an account on this number',
    createTitle: 'Your account',
    createLead: 'Enter your name as it appears on your medical registration.',
  },
}

function StepLabel({ mode }: { mode: PhoneAuthMode }) {
  return copy[mode].step ? <p className="meta step-label">{copy[mode].step}</p> : null
}

function MobileStep({ mode, onSent }: { mode: PhoneAuthMode; onSent: (mobile: string, developmentCode: string | null) => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const mobile = String(new FormData(event.currentTarget).get('mobileNumber') ?? '')
    setBusy(true)
    setError(null)
    try {
      const result = await api<OtpRequestResponse>('/auth/otp/request', { method: 'POST', body: { mobileNumber: mobile } })
      onSent(mobile, result.developmentCode)
    } catch (e) {
      setError(e instanceof ApiError && e.fieldErrors.mobileNumber ? e.fieldErrors.mobileNumber.join(' ') : errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <form method="post" onSubmit={handleSubmit}>
      <StepLabel mode={mode} />
      <h1>{copy[mode].mobileTitle}</h1>
      <p className="lead">{copy[mode].mobileLead}</p>
      <FormAlert message={error} />
      <TextField
        name="mobileNumber"
        label="Mobile number"
        hint="10-digit Indian mobile number, for example 98765 43210"
        type="tel"
        autoComplete="tel-national"
        inputMode="tel"
        enterKeyHint="send"
        pattern="[0-9+\s\-]{10,16}"
        autoFocus
      />
      <button type="submit" className="button primary" disabled={busy}>
        {busy ? 'Sending code…' : 'Send code'}
      </button>
    </form>
  )
}

interface OtpCredentialLike { code: string }

function CodeStep({
  mobile, developmentCode, onVerified, onChangeNumber,
}: {
  mobile: string
  developmentCode: string | null
  onVerified: (result: OtpVerifyResponse) => void
  onChangeNumber: () => void
}) {
  const formRef = useRef<HTMLFormElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  const webOtpAbort = useRef<AbortController | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(developmentCode ? `Development code: ${developmentCode}` : null)
  const [attempt, setAttempt] = useState(0)

  // WebOTP fills the code automatically on supporting Android browsers; others rely on autocomplete="one-time-code".
  useEffect(() => {
    if (!('OTPCredential' in window)) return
    const controller = new AbortController()
    webOtpAbort.current = controller
    navigator.credentials
      .get({ otp: { transport: ['sms'] }, signal: controller.signal } as CredentialRequestOptions)
      .then((credential) => {
        const otp = credential as unknown as OtpCredentialLike | null
        if (!otp || !inputRef.current) return
        inputRef.current.value = otp.code
        formRef.current?.requestSubmit()
      })
      .catch(() => {
        // Aborted on submit, resend or unmount, or dismissed by the user.
      })
    return () => controller.abort()
  }, [attempt])

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    webOtpAbort.current?.abort()
    const code = String(new FormData(event.currentTarget).get('otp') ?? '')
    setBusy(true)
    setError(null)
    try {
      onVerified(await api<OtpVerifyResponse>('/auth/otp/verify', { method: 'POST', body: { mobileNumber: mobile, code } }))
    } catch (e) {
      setError(errorMessage(e))
      setBusy(false)
    }
  }

  async function resend() {
    setError(null)
    try {
      const result = await api<OtpRequestResponse>('/auth/otp/request', { method: 'POST', body: { mobileNumber: mobile } })
      setNotice(result.developmentCode ? `New development code: ${result.developmentCode}` : 'A new code has been sent.')
      setAttempt((n) => n + 1)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <form ref={formRef} method="post" onSubmit={handleSubmit}>
      <h1>Enter your code</h1>
      <p className="lead">
        Sent to <strong>{mobile}</strong>.{' '}
        <button type="button" className="link-button" onClick={onChangeNumber}>Change number</button>
      </p>
      {notice && <p className="alert alert-info" role="status">{notice}</p>}
      <FormAlert message={error} />
      <div className="field">
        <label htmlFor="otp">Verification code</label>
        <p id="otp-hint" className="hint">Enter the 6-digit code from the SMS. It expires in 5 minutes.</p>
        <input
          ref={inputRef}
          id="otp"
          name="otp"
          type="text"
          className="otp-input"
          inputMode="numeric"
          autoComplete="one-time-code"
          enterKeyHint="done"
          pattern="\d{6}"
          maxLength={6}
          aria-describedby="otp-hint"
          required
          autoFocus
        />
      </div>
      <div className="actions">
        <button type="submit" className="button primary" disabled={busy}>{busy ? 'Checking…' : 'Verify code'}</button>
        <button type="button" className="button" onClick={() => void resend()}>Resend code</button>
      </div>
    </form>
  )
}

/** Super Admins confirm with an authenticator app code (PRD §27.4), enrolling on first sign-in. */
function MfaStep({
  challenge, onSignedIn, onRestart,
}: {
  challenge: MfaChallenge
  onSignedIn: (token: TokenResponse) => void
  onRestart: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const code = String(new FormData(event.currentTarget).get('totp') ?? '')
    setBusy(true)
    setError(null)
    try {
      onSignedIn(await api<TokenResponse>('/auth/mfa', { method: 'POST', body: { mfaTicket: challenge.mfaTicket, code } }))
    } catch (e) {
      setError(errorMessage(e))
      setBusy(false)
      if (e instanceof ApiError && e.status === 401) onRestart()
    }
  }

  return (
    <form method="post" onSubmit={handleSubmit}>
      <h1>Two-step verification</h1>
      {challenge.enrollment ? (
        <div className="alert alert-info">
          <p><strong>Set up your authenticator app.</strong> Administrator accounts need a second step at every sign-in.</p>
          <p>In Google Authenticator, Microsoft Authenticator or a similar app, add an account with this setup key:</p>
          <p className="code setup-key">{challenge.enrollment.secret.replace(/(.{4})/g, '$1 ').trim()}</p>
          <p className="hint">On a phone with the app installed you can also <a href={challenge.enrollment.otpAuthUri}>open the setup link</a>.</p>
        </div>
      ) : (
        <p className="lead">Enter the 6-digit code from your authenticator app.</p>
      )}
      <FormAlert message={error} />
      <div className="field">
        <label htmlFor="totp">Authenticator code</label>
        <input id="totp" name="totp" type="text" className="otp-input" inputMode="numeric" autoComplete="one-time-code"
          pattern="\d{6}" maxLength={6} required autoFocus />
      </div>
      <button type="submit" className="button primary" disabled={busy}>{busy ? 'Checking…' : 'Verify and sign in'}</button>
    </form>
  )
}

function AccountStep({
  mode, ticket, accounts, onCreate, onSignedIn, onMfa,
}: {
  mode: PhoneAuthMode
  ticket: string
  accounts: AccountSummary[]
  onCreate: () => void
  onSignedIn: (token: TokenResponse) => void
  onMfa: (challenge: MfaChallenge) => void
}) {
  const [busyId, setBusyId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function choose(userId: string) {
    setBusyId(userId)
    setError(null)
    try {
      const result = await api<TokenResponse | MfaChallenge>('/auth/session', { method: 'POST', body: { phoneTicket: ticket, userId } })
      if ('mfaRequired' in result) onMfa(result)
      else onSignedIn(result)
    } catch (e) {
      setError(errorMessage(e))
      setBusyId(null)
    }
  }

  return (
    <div>
      <StepLabel mode={mode} />
      <h1>{copy[mode].accountsTitle}</h1>
      <p className="lead">
        {mode === 'signin'
          ? 'This number is used by more than one person in your family, or by one account.'
          : mode === 'doctor'
            ? 'Continue with your account to add your professional details, or create a separate account.'
            : 'Sign in to the account below, or create another one for a different person on this number.'}
      </p>
      <FormAlert message={error} />
      <ul className="account-list">
        {accounts.map((a) => (
          <li key={a.userId}>
            <button type="button" className="account-button" disabled={busyId !== null} onClick={() => void choose(a.userId)}>
              <span className="account-name">{a.fullName}</span>
              {a.roles.length > 0 && <span className="account-roles">{a.roles.join(', ')}</span>}
              <span aria-hidden="true">→</span>
            </button>
          </li>
        ))}
      </ul>
      <button type="button" className="button" onClick={onCreate}>Create another account on this number</button>
    </div>
  )
}

function CreateAccountStep({
  mode, ticket, onCreated, onBack,
}: {
  mode: PhoneAuthMode
  ticket: string
  onCreated: (token: TokenResponse) => void
  onBack?: () => void
}) {
  const [reference, setReference] = useState<ReferenceData | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({})

  useEffect(() => {
    loadReference().then(setReference).catch((e: unknown) => setError(errorMessage(e)))
  }, [])

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!reference) return
    const data = new FormData(event.currentTarget)
    const text = (name: string) => String(data.get(name) ?? '').trim() || null
    setBusy(true)
    setError(null)
    try {
      onCreated(await api<TokenResponse>('/auth/accounts', {
        method: 'POST',
        body: {
          phoneTicket: ticket,
          fullName: text('fullName'),
          dateOfBirth: text('dateOfBirth'),
          gender: text('gender'),
          city: text('city'),
          email: text('email'),
          privacyNoticeVersion: reference.legalVersions.privacyNotice,
          termsVersion: reference.legalVersions.terms,
          intent: mode === 'doctor' ? 'Doctor' : 'Patient',
        },
      }))
    } catch (e) {
      if (e instanceof ApiError) setFieldErrors(e.fieldErrors)
      setError(errorMessage(e))
      setBusy(false)
    }
  }

  return (
    <form method="post" onSubmit={handleSubmit} onInput={clearFieldErrorOnInput(setFieldErrors)}>
      <StepLabel mode={mode} />
      <h1>{copy[mode].createTitle}</h1>
      <p className="lead">{copy[mode].createLead}</p>
      <FormAlert message={error} />
      <TextField name="fullName" label="Full name" autoComplete="name" maxLength={120} errors={fieldErrors.fullName} autoFocus />
      <TextField name="dateOfBirth" label="Date of birth" type="date" optional errors={fieldErrors.dateOfBirth} autoComplete="bday" />
      <fieldset className="field">
        <legend>Gender <span className="optional">(optional)</span></legend>
        <div className="radio-row">
          {([['Female', 'Female'], ['Male', 'Male'], ['Other', 'Other'], ['PreferNotToSay', 'Prefer not to say']] as const).map(([value, label]) => (
            <label key={value} className="radio">
              <input type="radio" name="gender" value={value} /> {label}
            </label>
          ))}
        </div>
      </fieldset>
      <TextField name="city" label="City" optional autoComplete="address-level2" maxLength={80} errors={fieldErrors.city} />
      <TextField name="email" label="Email" type="email" optional autoComplete="email" spellCheck={false} errors={fieldErrors.email} />
      <label className="checkbox">
        <input type="checkbox" name="consent" required />
        <span>
          I agree to the Privacy Notice ({reference?.legalVersions.privacyNotice ?? '…'}) and Terms of Use (
          {reference?.legalVersions.terms ?? '…'}), including use of my details to manage appointments.
        </span>
      </label>
      {(fieldErrors.privacyNoticeVersion || fieldErrors.termsVersion) && (
        <p className="field-error" role="alert">The terms have been updated. Reload the page to review the current version.</p>
      )}
      <div className="actions">
        <button type="submit" className="button primary" disabled={busy || !reference}>{busy ? 'Creating…' : 'Create account'}</button>
        {onBack && <button type="button" className="button" onClick={onBack}>Back</button>}
      </div>
    </form>
  )
}
