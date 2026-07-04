import axios from 'axios'

/**
 * Extracts the user-facing error message from an Axios error response.
 * Backend returns {success: false, message, errorCode} on errors.
 */
export function getApiErrorMessage(error: unknown): string {
  if (axios.isAxiosError(error)) {
    const data = error.response?.data as { message?: string } | undefined
    if (data?.message) return data.message
    return error.message
  }
  if (error instanceof Error) return error.message
  return 'Unknown error occurred.'
}
