import { useCallback, useEffect, useMemo, useReducer, useRef } from 'react'
import type { DiscountType, PosCatalogItemDto } from '../api/types'
import { posStorage, type CartLine, type TerminalCtx } from '../lib/posStorage'

type Action =
  | { kind: 'hydrate'; lines: CartLine[] }
  | { kind: 'add'; item: PosCatalogItemDto }
  | { kind: 'setQty'; variantId: string; qty: number }
  | { kind: 'remove'; variantId: string }
  | { kind: 'setDiscount'; variantId: string; discount: { type: DiscountType; value: number } }
  | { kind: 'clear' }

const round3 = (n: number) => Math.round(n * 1000) / 1000

function reducer(state: CartLine[], action: Action): CartLine[] {
  switch (action.kind) {
    case 'hydrate':
      return action.lines
    case 'add': {
      const { item } = action
      const existing = state.find((l) => l.variantId === item.productVariantId)
      if (existing) {
        return state.map((l) =>
          l.variantId === item.productVariantId ? { ...l, quantity: round3(l.quantity + 1) } : l,
        )
      }
      return [
        ...state,
        {
          variantId: item.productVariantId,
          productId: item.productId,
          name: item.productName,
          variantName: item.variantName,
          sku: item.sku,
          unitPrice: item.sellingPrice,
          quantity: 1,
          discount: { type: 'None', value: 0 },
        },
      ]
    }
    case 'setQty': {
      const qty = round3(action.qty)
      if (!(qty > 0)) return state.filter((l) => l.variantId !== action.variantId)
      return state.map((l) => (l.variantId === action.variantId ? { ...l, quantity: qty } : l))
    }
    case 'remove':
      return state.filter((l) => l.variantId !== action.variantId)
    case 'setDiscount':
      return state.map((l) =>
        l.variantId === action.variantId ? { ...l, discount: action.discount } : l,
      )
    case 'clear':
      return []
  }
}

export interface PosCart {
  lines: CartLine[]
  addItem: (item: PosCatalogItemDto) => void
  setQty: (variantId: string, qty: number) => void
  removeLine: (variantId: string) => void
  setLineDiscount: (variantId: string, d: { type: DiscountType; value: number }) => void
  clear: () => void
  isEmpty: boolean
}

/**
 * POS cart state (useReducer) mirrored to `localStorage`, scoped to the exact terminal
 * (`tenant/branch/register/session`). Rehydrates on mount; prunes only this register's stale
 * sessions. `clear()` also drops the persisted unresolved checkout-attempt id.
 */
export function usePosCart(ctx: TerminalCtx | null): PosCart {
  const [lines, dispatch] = useReducer(reducer, [])
  const ctxKey = ctx ? posStorage.cartKey(ctx) : null
  const hydratedFor = useRef<string | null>(null)

  useEffect(() => {
    if (!ctx || !ctxKey || hydratedFor.current === ctxKey) return
    hydratedFor.current = ctxKey
    dispatch({ kind: 'hydrate', lines: posStorage.readCart(ctx) ?? [] })
    posStorage.pruneStaleForRegister(ctx)
  }, [ctx, ctxKey])

  useEffect(() => {
    if (!ctx || hydratedFor.current !== ctxKey) return
    posStorage.writeCart(ctx, lines)
  }, [ctx, ctxKey, lines])

  const addItem = useCallback((item: PosCatalogItemDto) => dispatch({ kind: 'add', item }), [])
  const setQty = useCallback(
    (variantId: string, qty: number) => dispatch({ kind: 'setQty', variantId, qty }),
    [],
  )
  const removeLine = useCallback(
    (variantId: string) => dispatch({ kind: 'remove', variantId }),
    [],
  )
  const setLineDiscount = useCallback(
    (variantId: string, d: { type: DiscountType; value: number }) =>
      dispatch({ kind: 'setDiscount', variantId, discount: d }),
    [],
  )
  const clear = useCallback(() => {
    dispatch({ kind: 'clear' })
    if (ctx) {
      posStorage.clearCart(ctx)
      posStorage.clearAttemptId(ctx)
    }
  }, [ctx])

  return useMemo(
    () => ({
      lines,
      addItem,
      setQty,
      removeLine,
      setLineDiscount,
      clear,
      isEmpty: lines.length === 0,
    }),
    [lines, addItem, setQty, removeLine, setLineDiscount, clear],
  )
}
