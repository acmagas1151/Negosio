/** Builds a `/sales` URL carrying the exact filters a Performance report row represents. Always
 * passes fromUtc/toUtc explicitly (never a period label or date-only string) — see
 * docs/superpowers/specs/2026-09-27-reports-fulfillment-polish.md §2 for why: SalesPage's own
 * date pickers convert using the browser's local timezone, while every Performance report resolves
 * its range server-side in Asia/Manila, so only passing the already-resolved UTC instants through
 * guarantees the drill-down shows exactly the sales the report row summarized. */
export function buildSalesDrilldownUrl(params: {
  fromUtc: string
  toUtc: string
  branchId?: string
  registerId?: string
  cashierUserId?: string
}): string {
  const search = new URLSearchParams()
  search.set('fromUtc', params.fromUtc)
  search.set('toUtc', params.toUtc)
  if (params.branchId) search.set('branchId', params.branchId)
  if (params.registerId) search.set('registerId', params.registerId)
  if (params.cashierUserId) search.set('cashierUserId', params.cashierUserId)
  return `/sales?${search.toString()}`
}
