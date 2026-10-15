import { useState, type FormEvent, type ReactNode } from 'react'
import { ApiError, errorMessage } from '../api/client'
import { FormAlert } from './Fields'
import { useModalDialog } from './useModalDialog'

/**
 * A modal form built on <dialog>. Esc and Cancel close it; the submit handler receives the form data.
 * Server field errors are passed back to the content through `fieldErrors`.
 */
export function ModalForm({
  title, submitLabel, danger = false, onSubmit, onClose, children,
}: {
  title: string
  submitLabel: string
  danger?: boolean
  onSubmit: (data: FormData) => Promise<void>
  onClose: () => void
  children: (fieldErrors: Record<string, string[]>) => ReactNode
}) {
  const dialogRef = useModalDialog()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({})

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await onSubmit(new FormData(event.currentTarget))
      dialogRef.current?.close()
    } catch (e) {
      if (e instanceof ApiError) setFieldErrors(e.fieldErrors)
      setError(errorMessage(e))
      setBusy(false)
    }
  }

  return (
    <dialog ref={dialogRef} className="dialog" aria-labelledby="modal-title" onClose={onClose}>
      <form method="dialog" onSubmit={handleSubmit}>
        <h2 id="modal-title">{title}</h2>
        <FormAlert message={error} />
        {children(fieldErrors)}
        <div className="actions">
          <button type="submit" className={`button ${danger ? 'danger' : 'primary'}`} disabled={busy}>
            {busy ? 'Saving…' : submitLabel}
          </button>
          <button type="button" className="button" onClick={() => dialogRef.current?.close()} disabled={busy}>Cancel</button>
        </div>
      </form>
    </dialog>
  )
}

export function StatusBadge({ label, tone }: { label: string; tone: 'info' | 'success' | 'warning' | 'danger' | 'neutral' }) {
  return <span className={`status tone-${tone}`}>{label}</span>
}
