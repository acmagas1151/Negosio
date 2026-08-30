// Currency is hard-coded to PHP for this phase — Negosio is not yet multi-currency
// and tenants cannot configure it. A later settings slice adds a currency argument here.
const pesoFormatter = new Intl.NumberFormat('en-PH', {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

export function formatMoney(n: number): string {
  return `₱${pesoFormatter.format(n)}`
}

export function formatRange(min: number, max: number, fmt: (n: number) => string): string {
  return min === max ? fmt(min) : `${fmt(min)} – ${fmt(max)}`
}

/** Gross margin as a whole-number percent, or null when cost is unknown / selling is non-positive. */
export function formatMarginPct(cost: number | null, selling: number): string | null {
  if (cost === null || selling <= 0) return null
  return `${Math.round(((selling - cost) / selling) * 100)}%`
}
