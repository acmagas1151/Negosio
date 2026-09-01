import type { DiscountType } from '../api/types'

export interface TerminalCtx {
  tenantId: string
  branchId: string
  registerId: string
  registerSessionId: string
}

export interface CartLine {
  variantId: string
  productId: string
  name: string
  variantName: string | null
  sku: string | null
  unitPrice: number
  quantity: number
  discount: { type: DiscountType; value: number }
}

const CART_PREFIX = 'negosio.pos.cart.v1.'
const ATTEMPT_PREFIX = 'negosio.pos.attempt.v1.'
const REGISTER_PREFIX = 'negosio.pos.register.v1.'
const BRANCH_PREFIX = 'negosio.pos.branch.v1.'

/** tenant/branch/register/session — cart and checkout-attempt id are both scoped to this. */
const scope = (c: TerminalCtx) =>
  `${c.tenantId}/${c.branchId}/${c.registerId}/${c.registerSessionId}`
/** tenant/branch/register — everything for one register, any session. */
const registerScope = (c: TerminalCtx) => `${c.tenantId}/${c.branchId}/${c.registerId}/`

function safeGet(key: string): string | null {
  try {
    return localStorage.getItem(key)
  } catch {
    return null
  }
}
function safeSet(key: string, value: string): void {
  try {
    localStorage.setItem(key, value)
  } catch {
    /* private mode / quota — persistence is best-effort */
  }
}
function safeRemove(key: string): void {
  try {
    localStorage.removeItem(key)
  } catch {
    /* ignore */
  }
}

export const posStorage = {
  cartKey: (c: TerminalCtx) => `${CART_PREFIX}${scope(c)}`,
  attemptKey: (c: TerminalCtx) => `${ATTEMPT_PREFIX}${scope(c)}`,

  readCart(c: TerminalCtx): CartLine[] | null {
    const raw = safeGet(posStorage.cartKey(c))
    if (!raw) return null
    try {
      const parsed = JSON.parse(raw)
      return Array.isArray(parsed) ? (parsed as CartLine[]) : null
    } catch {
      return null
    }
  },
  writeCart: (c: TerminalCtx, lines: CartLine[]) =>
    safeSet(posStorage.cartKey(c), JSON.stringify(lines)),
  clearCart: (c: TerminalCtx) => safeRemove(posStorage.cartKey(c)),

  /** The opaque, unresolved checkout-attempt id. Never store payment details here. */
  readAttemptId: (c: TerminalCtx) => safeGet(posStorage.attemptKey(c)),
  writeAttemptId: (c: TerminalCtx, id: string) => safeSet(posStorage.attemptKey(c), id),
  clearAttemptId: (c: TerminalCtx) => safeRemove(posStorage.attemptKey(c)),

  /**
   * Remove cart + attempt-id keys that belong to the SAME register but an older session.
   * Scoped by `tenant/branch/register/` — another register's active cart is never touched.
   */
  pruneStaleForRegister(c: TerminalCtx) {
    const keepCart = posStorage.cartKey(c)
    const keepAttempt = posStorage.attemptKey(c)
    const reg = registerScope(c)
    try {
      for (let i = localStorage.length - 1; i >= 0; i--) {
        const k = localStorage.key(i)
        if (!k) continue
        const isOurRegisterCart = k.startsWith(CART_PREFIX + reg) && k !== keepCart
        const isOurRegisterAttempt = k.startsWith(ATTEMPT_PREFIX + reg) && k !== keepAttempt
        if (isOurRegisterCart || isOurRegisterAttempt) localStorage.removeItem(k)
      }
    } catch {
      /* ignore */
    }
  },

  readRegister: (c: { tenantId: string; branchId: string }) =>
    safeGet(`${REGISTER_PREFIX}${c.tenantId}/${c.branchId}`),
  writeRegister: (c: { tenantId: string; branchId: string }, registerId: string) =>
    safeSet(`${REGISTER_PREFIX}${c.tenantId}/${c.branchId}`, registerId),

  /** Last branch an all-branch operator (Owner/Admin) chose for the POS. */
  readBranch: (c: { tenantId: string }) => safeGet(`${BRANCH_PREFIX}${c.tenantId}`),
  writeBranch: (c: { tenantId: string }, branchId: string) =>
    safeSet(`${BRANCH_PREFIX}${c.tenantId}`, branchId),
  clearBranch: (c: { tenantId: string }) => safeRemove(`${BRANCH_PREFIX}${c.tenantId}`),
}
