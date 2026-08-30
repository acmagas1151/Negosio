import { ApiError } from '../api/client'

/**
 * Flatten an ApiError's field errors to `{ fieldnamelower: firstMessage }`.
 * Returns `{}` for anything that isn't a validation ApiError.
 */
export function fieldErrorsFrom(error: unknown): Record<string, string> {
  if (!(error instanceof ApiError) || !error.fieldErrors) return {}
  const out: Record<string, string> = {}
  for (const [key, messages] of Object.entries(error.fieldErrors)) {
    if (messages.length > 0) out[key.toLowerCase()] = messages[0]
  }
  return out
}

const CODE_TO_FIELD: Record<string, string> = {
  CATEGORY_ALREADY_EXISTS: 'name',
  SKU_ALREADY_EXISTS: 'sku',
  BARCODE_ALREADY_EXISTS: 'barcode',
}

/** Which form field a conflict `code` should attach to, or null to fall back to a toast. */
export function mapCodeToField(code: string): string | null {
  return CODE_TO_FIELD[code] ?? null
}
