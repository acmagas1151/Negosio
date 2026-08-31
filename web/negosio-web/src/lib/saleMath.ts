import type { DiscountType, TaxSettingsDto } from '../api/types'

/**
 * PREVIEW ONLY. Mirrors src/Negosio.Application/Pos/SaleLineCalculator.cs so the cart can show
 * an estimated total before checkout. The backend recomputes every amount on POST /api/pos/checkout
 * and its SaleResultDto is authoritative — never reconcile against this, just show server values.
 */
export function roundMoney(n: number): number {
  // half-up at 2dp
  return (Math.sign(n) * Math.round(Math.abs(n) * 100 + 1e-9)) / 100
}

export interface LineForMath {
  unitPrice: number
  quantity: number
  discount: { type: DiscountType; value: number }
}

export function calcLine(
  input: LineForMath & { taxRatePercent: number; pricesIncludeTax: boolean },
): { gross: number; discount: number; tax: number; net: number } {
  const { unitPrice, quantity, discount, taxRatePercent, pricesIncludeTax } = input
  const gross = roundMoney(unitPrice * quantity)

  let discountAmount = 0
  if (discount.type === 'Percentage') {
    const v = Math.min(Math.max(discount.value, 0), 100)
    discountAmount = roundMoney((gross * v) / 100)
  } else if (discount.type === 'FixedAmount') {
    discountAmount = Math.min(roundMoney(Math.max(discount.value, 0)), gross)
  }

  const taxable = gross - discountAmount
  const net = taxable
  let tax = 0
  if (taxRatePercent > 0) {
    if (pricesIncludeTax) {
      const netOfTax = roundMoney(taxable / (1 + taxRatePercent / 100))
      tax = taxable - netOfTax
    } else {
      tax = roundMoney((taxable * taxRatePercent) / 100)
    }
  }
  return { gross, discount: discountAmount, tax, net }
}

export function calcTotals(
  lines: LineForMath[],
  tax: TaxSettingsDto,
): { subtotal: number; discountTotal: number; taxTotal: number; grandTotal: number } {
  let subtotal = 0
  let discountTotal = 0
  let taxTotal = 0
  for (const l of lines) {
    const r = calcLine({
      ...l,
      taxRatePercent: tax.taxRatePercent,
      pricesIncludeTax: tax.pricesIncludeTax,
    })
    subtotal += r.gross
    discountTotal += r.discount
    taxTotal += r.tax
  }
  subtotal = roundMoney(subtotal)
  discountTotal = roundMoney(discountTotal)
  taxTotal = roundMoney(taxTotal)
  const grandTotal = tax.pricesIncludeTax
    ? roundMoney(subtotal - discountTotal)
    : roundMoney(subtotal - discountTotal + taxTotal)
  return { subtotal, discountTotal, taxTotal, grandTotal }
}
