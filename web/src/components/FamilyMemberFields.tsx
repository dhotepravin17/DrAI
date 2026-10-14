import { useState } from 'react'
import { relationshipLabels, type PersonDto, type Relationship } from '../api/queueTypes'
import { isMinor } from '../people'
import { todayIst } from '../format'
import { SelectField, TextField } from './Fields'

const relationships = (Object.keys(relationshipLabels) as Relationship[]).filter((r) => r !== 'Self')

/** Name, relationship, date of birth and, for children, the guardian's consent (DPDP Act). */
export function FamilyMemberFields({ errors, person }: { errors: Record<string, string[]>; person?: PersonDto }) {
  const [dob, setDob] = useState(person?.dateOfBirth ?? '')
  const minor = isMinor(dob)

  return (
    <>
      <TextField name="fullName" label="Full name of the person visiting the doctor" autoComplete="off" maxLength={120}
        defaultValue={person?.fullName} errors={errors.fullName} />
      <SelectField name="relationship" label="Relationship to you" defaultValue={person?.relationship ?? ''}
        options={relationships.map((r) => ({ value: r, label: relationshipLabels[r] }))} errors={errors.relationship} />
      <TextField name="dateOfBirth" label="Date of birth" type="date" optional max={todayIst()} value={dob}
        onChange={(e) => setDob(e.target.value)} hint="Helps the clinic and decides who can see their visit history"
        errors={errors.dateOfBirth} />
      <fieldset className="field">
        <legend>Gender <span className="optional">(optional)</span></legend>
        <div className="radio-row">
          {([['Female', 'Female'], ['Male', 'Male'], ['Other', 'Other']] as const).map(([value, label]) => (
            <label key={value} className="radio">
              <input type="radio" name="gender" value={value} defaultChecked={person?.gender === value} /> {label}
            </label>
          ))}
        </div>
      </fieldset>
      {minor && (
        <label className="checkbox">
          <input type="checkbox" name="guardianConsent" required aria-invalid={errors.guardianConsent ? true : undefined} />
          <span>I am this child's parent or legal guardian and consent to their details being used to manage appointments.</span>
        </label>
      )}
      {errors.guardianConsent && <p className="field-error" role="alert">{errors.guardianConsent.join(' ')}</p>}
      {!minor && dob && (
        <p className="hint">
          Adults: you will see only the visits you book for them. They can link their own account to share their full history with you.
        </p>
      )}
    </>
  )
}
