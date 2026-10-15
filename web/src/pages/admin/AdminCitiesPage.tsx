import { useState, type FormEvent } from 'react'
import { api, ApiError, errorMessage } from '../../api/client'
import type { AdminCityRow } from '../../api/types'
import { useApi } from '../../api/useApi'
import { FormAlert, TextField } from '../../components/Fields'
import { Loading } from '../../components/Layout'
import { clearFieldErrorOnInput } from '../../format'

const CAPACITY = 9999
const WARNING_THRESHOLD = 9000

export function AdminCitiesPage() {
  const { data: cities, error, reload } = useApi<AdminCityRow[]>('/admin/cities')
  const [formError, setFormError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({})
  const [busy, setBusy] = useState(false)
  const [formKey, setFormKey] = useState(0)

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const data = new FormData(event.currentTarget)
    setBusy(true)
    setFormError(null)
    try {
      await api('/admin/cities', {
        method: 'POST',
        body: { code: data.get('code'), name: data.get('name'), state: data.get('state') },
      })
      setFieldErrors({})
      setFormKey((k) => k + 1)
      reload()
    } catch (e) {
      if (e instanceof ApiError) setFieldErrors(e.fieldErrors)
      setFormError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <section>
      <title>City codes · MediCare Plus</title>
      <h1>City codes</h1>
      <p className="lead">Doctor IDs use the city code, for example NGP-Dr-0001. Codes cannot be changed once doctors are issued IDs.</p>
      {error ? <p className="alert alert-error" role="alert">{errorMessage(error)}</p> : null}
      {!cities && !error && <Loading />}
      {cities && (
        <div className="table-wrap">
          <table>
            <thead>
              <tr><th scope="col">Code</th><th scope="col">City</th><th scope="col">State</th><th scope="col">Doctor IDs issued</th></tr>
            </thead>
            <tbody>
              {cities.map((c) => (
                <tr key={c.code}>
                  <td className="code">{c.code}</td>
                  <td>{c.name}</td>
                  <td>{c.state}</td>
                  <td>
                    {c.issuedDoctorIds} of {CAPACITY}
                    {c.issuedDoctorIds >= WARNING_THRESHOLD && <span className="badge badge-warning">Near capacity</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <form key={formKey} method="post" className="panel narrow" onSubmit={handleSubmit} onInput={clearFieldErrorOnInput(setFieldErrors)}>
        <h2>Add a city</h2>
        <FormAlert message={formError} />
        <TextField name="code" label="City code" hint="Exactly three letters, for example NGP" pattern="[A-Za-z]{3}" maxLength={3}
          autoCapitalize="characters" autoCorrect="off" spellCheck={false} autoComplete="off" errors={fieldErrors.code} />
        <TextField name="name" label="City name" maxLength={80} autoComplete="off" errors={fieldErrors.name} />
        <TextField name="state" label="State" maxLength={80} defaultValue="Maharashtra" autoComplete="off" errors={fieldErrors.state} />
        <button type="submit" className="button primary" disabled={busy}>{busy ? 'Adding…' : 'Add city'}</button>
      </form>
    </section>
  )
}
