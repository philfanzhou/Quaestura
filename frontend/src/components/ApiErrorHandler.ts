import axios, { type AxiosError } from 'axios'

/**
 * Extracts the user-facing error message from an Axios error response.
 *
 * Two response formats coexist:
 * - Explicit endpoint failures still return the legacy business envelope
 *   `{success: false, message, errorCode}` (`data.message` is used verbatim).
 * - Exceptions escaping an /admin endpoint are converted by the backend
 *   ServiceMantle Problem Details middleware into `application/problem+json`
 *   with `{type, title, status, correlationId, errorCode, quaesturaErrorCode?,
 *   quaesturaValidationErrors?}`. Only the fixed `title` and the whitelisted
 *   `quaesturaValidationErrors` field (developer-declared validation texts)
 *   are ever displayed; no raw exception text is shown.
 */
export function getApiErrorMessage(error: unknown): string {
  if (axios.isAxiosError(error)) {
    const data = error.response?.data as
      | { message?: string; quaesturaValidationErrors?: string; title?: string }
      | undefined
    if (data?.message) return data.message
    if (isProblemDetails(error)) {
      if (data?.quaesturaValidationErrors) return data.quaesturaValidationErrors
      if (data?.title) return data.title
    }
    return error.message
  }
  if (error instanceof Error) return error.message
  return 'Unknown error occurred.'
}

function isProblemDetails(error: AxiosError): boolean {
  const contentType = error.response?.headers?.['content-type']
  return typeof contentType === 'string' && contentType.includes('application/problem+json')
}
