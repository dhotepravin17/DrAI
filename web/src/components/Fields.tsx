import type { InputHTMLAttributes, ReactNode, SelectHTMLAttributes, TextareaHTMLAttributes } from 'react'

interface FieldProps {
  name: string
  label: string
  hint?: ReactNode
  errors?: string[]
  optional?: boolean
}

function describe(name: string, hint: ReactNode, errors?: string[]) {
  const id = `field-${name}`
  const hintId = hint ? `${id}-hint` : undefined
  const errorId = errors?.length ? `${id}-error` : undefined
  return {
    id,
    hintId,
    errorId,
    describedBy: [hintId, errorId].filter(Boolean).join(' ') || undefined,
    invalid: errors?.length ? true : undefined,
  }
}

function FieldShell({
  name, label, hint, errors, optional, ids, children,
}: FieldProps & { ids: ReturnType<typeof describe>; children: ReactNode }) {
  return (
    <div className="field" data-field={name}>
      <label htmlFor={ids.id}>
        {label}
        {optional && <span className="optional"> (optional)</span>}
      </label>
      {hint && <p id={ids.hintId} className="hint">{hint}</p>}
      {children}
      {errors?.length ? (
        <p id={ids.errorId} className="field-error" role="alert">
          <span aria-hidden="true">⚠ </span>
          {errors.join(' ')}
        </p>
      ) : null}
    </div>
  )
}

export function TextField({
  name, label, hint, errors, optional, ...input
}: FieldProps & InputHTMLAttributes<HTMLInputElement>) {
  const ids = describe(name, hint, errors)
  return (
    <FieldShell {...{ name, label, hint, errors, optional, ids }}>
      <input
        id={ids.id}
        name={name}
        required={!optional}
        aria-describedby={ids.describedBy}
        aria-invalid={ids.invalid}
        {...input}
      />
    </FieldShell>
  )
}

export function TextAreaField({
  name, label, hint, errors, optional, ...input
}: FieldProps & TextareaHTMLAttributes<HTMLTextAreaElement>) {
  const ids = describe(name, hint, errors)
  return (
    <FieldShell {...{ name, label, hint, errors, optional, ids }}>
      <textarea
        id={ids.id}
        name={name}
        required={!optional}
        aria-describedby={ids.describedBy}
        aria-invalid={ids.invalid}
        rows={3}
        {...input}
      />
    </FieldShell>
  )
}

export function SelectField({
  name, label, hint, errors, optional, options, placeholder = 'Choose…', ...select
}: FieldProps & SelectHTMLAttributes<HTMLSelectElement> & {
  options: { value: string; label: string }[]
  placeholder?: string
}) {
  const ids = describe(name, hint, errors)
  return (
    <FieldShell {...{ name, label, hint, errors, optional, ids }}>
      <select
        id={ids.id}
        name={name}
        required={!optional}
        aria-describedby={ids.describedBy}
        aria-invalid={ids.invalid}
        {...select}
      >
        <option value="">{placeholder}</option>
        {options.map((o) => (
          <option key={o.value} value={o.value}>{o.label}</option>
        ))}
      </select>
    </FieldShell>
  )
}

export function FormAlert({ message }: { message: string | null }) {
  if (!message) return null
  return (
    <p className="alert alert-error" role="alert">
      {message}
    </p>
  )
}

