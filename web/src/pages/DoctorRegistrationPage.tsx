import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { api, ApiError, errorMessage, loadReference } from '../api/client'
import { rejectionLabels, statusLabels, type Declaration, type ReferenceData, type Registration } from '../api/types'
import { useAuth } from '../auth/authState'
import { FormAlert, SelectField, TextAreaField, TextField } from '../components/Fields'
import { clearFieldErrorOnInput } from '../format'
import { Loading } from '../components/Layout'

export function DoctorRegistrationPage() {
  const { refreshMe } = useAuth()
  const [reference, setReference] = useState<ReferenceData | null>(null)
  const [declaration, setDeclaration] = useState<Declaration | null>(null)
  const [registration, setRegistration] = useState<Registration | null>(null)
  const [loaded, setLoaded] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    Promise.all([
      loadReference(),
      api<Declaration>('/reference/doctor-declaration', { signal: controller.signal }),
      api<Registration>('/doctor-registration', { signal: controller.signal }).catch((e: unknown) => {
        if (e instanceof ApiError && e.status === 404) return null
        throw e
      }),
    ])
      .then(([ref, decl, reg]) => {
        setReference(ref)
        setDeclaration(decl)
        setRegistration(reg)
        setLoaded(true)
      })
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setLoadError(errorMessage(e))
      })
    return () => controller.abort()
  }, [])

  if (loadError) return <p className="alert alert-error" role="alert">{loadError}</p>
  if (!loaded || !reference || !declaration) return <Loading />

  const editable = !registration || registration.status === 'Draft' || registration.status === 'Rejected'

  return (
    <section className="panel">
      <title>Doctor registration · Smart Doctor Queue</title>
      {(!registration || registration.status === 'Draft') && <p className="meta step-label">Step 2 of 2 · Professional details</p>}
      <h1>Doctor registration</h1>
      {registration && <StatusBanner registration={registration} />}
      {editable ? (
        <RegistrationForm
          reference={reference}
          declaration={declaration}
          registration={registration}
          onSaved={(r) => {
            setRegistration(r)
            void refreshMe()
          }}
        />
      ) : (
        registration && <RegistrationSummary registration={registration} reference={reference} />
      )}
    </section>
  )
}

function StatusBanner({ registration: r }: { registration: Registration }) {
  switch (r.status) {
    case 'Draft':
      return <p className="alert alert-info">Your details are saved as a draft. Accept the declaration and submit them for verification.</p>
    case 'PendingVerification':
      return (
        <p className="alert alert-info" role="status">
          Submitted for verification (reference <strong>{r.submissionReference}</strong>). You will be able to accept bookings once a
          platform administrator verifies your Medical Registration ID.
        </p>
      )
    case 'Verified':
      return (
        <div className="alert alert-success" role="status">
          <p>You are verified. Your Doctor ID is <strong className="code">{r.doctorCode}</strong>.</p>
          <p><Link to={`/doctors/${r.doctorCode}`}>View your public profile</Link></p>
        </div>
      )
    case 'Rejected':
      return (
        <div className="alert alert-error" role="alert">
          <p><strong>Verification was not successful.</strong> {r.rejectionReason ? rejectionLabels[r.rejectionReason] : ''}</p>
          {r.statusNote && <p>Note from the reviewer: {r.statusNote}</p>}
          <p>Correct your details below and submit again.</p>
        </div>
      )
    case 'Suspended':
      return <p className="alert alert-error" role="alert">Your account is suspended. {r.statusNote} Contact platform support.</p>
    case 'Deactivated':
      return <p className="alert alert-error" role="alert">This doctor account has been deactivated. {r.statusNote}</p>
  }
}

function RegistrationSummary({ registration: r, reference }: { registration: Registration; reference: ReferenceData }) {
  const city = reference.cities.find((c) => c.code === r.primaryCityCode)?.name ?? r.primaryCityCode
  return (
    <dl className="details">
      <div><dt>Status</dt><dd>{statusLabels[r.status]}</dd></div>
      {r.doctorCode && <div><dt>Doctor ID</dt><dd className="code">{r.doctorCode}</dd></div>}
      <div><dt>Name</dt><dd>{r.fullName}</dd></div>
      <div><dt>Specialty</dt><dd>{r.specialty}</dd></div>
      <div><dt>Medical Registration ID</dt><dd>{r.medicalRegistrationId} ({r.medicalRegistrationAuthority})</dd></div>
      <div><dt>Primary city</dt><dd>{city}</dd></div>
      <div><dt>Clinic</dt><dd>{r.clinicName}, {r.clinicAddress}</dd></div>
    </dl>
  )
}

function RegistrationForm({
  reference, declaration, registration, onSaved,
}: {
  reference: ReferenceData
  declaration: Declaration
  registration: Registration | null
  onSaved: (r: Registration) => void
}) {
  const [busy, setBusy] = useState<'save' | 'submit' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({})
  const r = registration

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const submitter = (event.nativeEvent as SubmitEvent).submitter as HTMLButtonElement | null
    const intent = submitter?.value === 'save' ? 'save' : 'submit'
    const data = new FormData(event.currentTarget)
    const text = (name: string) => String(data.get(name) ?? '').trim() || null

    if (intent === 'submit' && data.get('declarationAccepted') !== 'on') {
      setFieldErrors({ declarationAccepted: ['You must accept the declaration to submit.'] })
      return
    }

    setBusy(intent)
    setError(null)
    setSaved(null)
    try {
      let result = await api<Registration>('/doctor-registration', {
        method: 'PUT',
        body: {
          fullName: text('fullName'),
          email: text('email'),
          specialty: text('specialty'),
          qualifications: text('qualifications'),
          medicalRegistrationId: text('medicalRegistrationId'),
          medicalRegistrationAuthority: text('medicalRegistrationAuthority'),
          primaryCityCode: text('primaryCityCode'),
          clinicName: text('clinicName'),
          clinicAddress: text('clinicAddress'),
          clinicPublicPhone: text('clinicPublicPhone'),
        },
      })
      if (intent === 'submit') {
        result = await api<Registration>('/doctor-registration/submit', {
          method: 'POST',
          body: { declarationAccepted: true, declarationVersion: declaration.version },
        })
      } else {
        setSaved('Draft saved.')
      }
      setFieldErrors({})
      onSaved(result)
    } catch (e) {
      if (e instanceof ApiError) setFieldErrors(e.fieldErrors)
      setError(errorMessage(e))
    } finally {
      setBusy(null)
    }
  }

  return (
    <form method="post" onSubmit={handleSubmit} onInput={clearFieldErrorOnInput(setFieldErrors)}>
      <FormAlert message={error} />
      {saved && <p className="alert alert-success" role="status">{saved}</p>}

      <fieldset>
        <legend>Your details</legend>
        <TextField name="fullName" label="Full name" hint="As it appears in your medical registration" autoComplete="name"
          maxLength={120} defaultValue={r?.fullName ?? ''} errors={fieldErrors.fullName} />
        <TextField name="email" label="Email" type="email" optional autoComplete="email" spellCheck={false}
          defaultValue={r?.email ?? ''} errors={fieldErrors.email} />
      </fieldset>

      <fieldset>
        <legend>Professional registration</legend>
        <SelectField name="medicalRegistrationAuthority" label="Registering council"
          hint="The National Medical Commission or the State Medical Council that issued your registration"
          options={reference.medicalAuthorities.map((a) => ({ value: a, label: a }))}
          defaultValue={r?.medicalRegistrationAuthority ?? ''} errors={fieldErrors.medicalRegistrationAuthority} />
        <TextField name="medicalRegistrationId" label="Medical Registration ID"
          hint="Your registration number. It is kept private and is not shown to patients."
          autoComplete="off" autoCorrect="off" autoCapitalize="characters" spellCheck={false} maxLength={40}
          defaultValue={r?.medicalRegistrationId ?? ''} errors={fieldErrors.medicalRegistrationId} />
        <SelectField name="specialty" label="Specialty" options={reference.specialties.map((s) => ({ value: s, label: s }))}
          defaultValue={r?.specialty ?? ''} errors={fieldErrors.specialty} />
        <TextField name="qualifications" label="Qualifications" hint="For example MBBS, MD (Paediatrics)" optional
          maxLength={200} defaultValue={r?.qualifications ?? ''} errors={fieldErrors.qualifications} />
      </fieldset>

      <fieldset>
        <legend>Primary clinic</legend>
        <SelectField name="primaryCityCode" label="City"
          hint="Your Doctor ID starts with this city's code and stays the same if you move later"
          options={reference.cities.map((c) => ({ value: c.code, label: `${c.name} (${c.code})` }))}
          defaultValue={r?.primaryCityCode ?? ''} errors={fieldErrors.primaryCityCode} />
        <TextField name="clinicName" label="Clinic name" autoComplete="organization" maxLength={120}
          defaultValue={r?.clinicName ?? ''} errors={fieldErrors.clinicName} />
        <TextAreaField name="clinicAddress" label="Clinic address" autoComplete="street-address" maxLength={300}
          defaultValue={r?.clinicAddress ?? ''} errors={fieldErrors.clinicAddress} />
        <TextField name="clinicPublicPhone" label="Clinic phone for patients" type="tel" optional autoComplete="off"
          hint="Shown on your public profile" maxLength={20}
          defaultValue={r?.clinicPublicPhone ?? ''} errors={fieldErrors.clinicPublicPhone} />
      </fieldset>

      <fieldset className="declaration">
        <legend>Declaration ({declaration.version})</legend>
        <p id="declaration-text" className="declaration-text">{declaration.text}</p>
        <label className="checkbox">
          <input type="checkbox" name="declarationAccepted" aria-describedby="declaration-text"
            aria-invalid={fieldErrors.declarationAccepted ? true : undefined} />
          <span>I have read and accept this declaration.</span>
        </label>
        {fieldErrors.declarationAccepted && (
          <p className="field-error" role="alert"><span aria-hidden="true">⚠ </span>{fieldErrors.declarationAccepted.join(' ')}</p>
        )}
        <p className="hint">
          Submitting also confirms you have read the Privacy Notice ({reference.legalVersions.privacyNotice}), the Terms of Use (
          {reference.legalVersions.terms}) and the professional verification policy. Accepting the declaration does not verify
          you; the platform checks your registration before you can accept bookings.
        </p>
      </fieldset>

      <div className="actions">
        <button type="submit" value="submit" className="button primary" disabled={busy !== null}>
          {busy === 'submit' ? 'Submitting…' : 'Submit for verification'}
        </button>
        <button type="submit" value="save" className="button" formNoValidate disabled={busy !== null}>
          {busy === 'save' ? 'Saving…' : 'Save draft'}
        </button>
      </div>
    </form>
  )
}
