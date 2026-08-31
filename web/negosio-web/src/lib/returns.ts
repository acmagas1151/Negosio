import type { SaleItemDto } from '../api/types'

/** Units still eligible for return on a sale line. */
export const returnableQty = (i: SaleItemDto) => i.quantity - i.returnedQuantity

export const hasReturnableQty = (items: SaleItemDto[]) =>
  items.some((i) => returnableQty(i) > 0)
