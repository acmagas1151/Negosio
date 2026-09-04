import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ApiError } from '../api/client'
import { posApi, salesApi, sessionsApi } from '../api/pos'
import type { CashMovementType } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { useCan } from '../lib/useCan'
import type { CurrentSaleRef } from '../lib/pos'
import { posStorage } from '../lib/posStorage'
import { BranchPicker } from '../components/pos/BranchPicker'
import { CashMovementModal } from '../components/pos/CashMovementModal'
import { CloseSessionModal } from '../components/pos/CloseSessionModal'
import { PosSessionGate } from '../components/pos/PosSessionGate'
import { PosShell } from '../components/pos/PosShell'
import { PosTerminal } from '../components/pos/PosTerminal'
import { RegisterPicker } from '../components/pos/RegisterPicker'
import { ErrorState, LoadingState } from '../components/ui'

function PosCentered({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex min-h-screen items-center justify-center bg-background p-4">{children}</div>
  )
}

function PosDenied() {
  return (
    <PosCentered>
      <div className="max-w-sm space-y-3 rounded-2xl border border-border bg-surface p-6 text-center shadow-card-lg">
        <h1 className="text-lg font-bold text-text-primary">No POS access</h1>
        <p className="text-[13px] text-text-secondary">
          Your role can&rsquo;t operate the point of sale. Ask an administrator if you need access.
        </p>
        <Link to="/dashboard" className="text-[13px] font-semibold text-primary-700 hover:underline">
          Back to dashboard
        </Link>
      </div>
    </PosCentered>
  )
}

export default function PosPage() {
  const { user } = useAuth()
  const canOperate = useCan('pos:operate')

  const contextQuery = useQuery({ queryKey: ['pos', 'context'], queryFn: posApi.context })

  const [branchOverride, setBranchOverride] = useState<string | null>(null)
  const [registerId, setRegisterId] = useState<string | null>(null)
  const [skipGate, setSkipGate] = useState(false)
  const [closeOpen, setCloseOpen] = useState(false)
  const [cashMovementType, setCashMovementType] = useState<CashMovementType | null>(null)
  const [pickerNotice, setPickerNotice] = useState<string | null>(null)
  // The terminal's "current sale" — real only, set by a successful checkout (and kept in sync when
  // that same sale is later voided). Cleared below whenever branch/register/session changes so it
  // never leaks across a different session.
  const [lastCompletedSale, setLastCompletedSale] = useState<CurrentSaleRef | null>(null)

  const ctx = contextQuery.data
  const persistedBranch =
    user && ctx?.canPickBranch ? posStorage.readBranch({ tenantId: user.tenantId }) : null
  const persistedBranchValid =
    persistedBranch && ctx?.branches.some((b) => b.id === persistedBranch) ? persistedBranch : null
  const branchId: string | null = ctx
    ? ctx.canPickBranch
      ? (branchOverride ?? persistedBranchValid ?? null)
      : ctx.branchId
    : null
  const branchName = ctx?.branches.find((b) => b.id === branchId)?.name ?? ctx?.branchName ?? null
  const branchCode = ctx?.branches.find((b) => b.id === branchId)?.code ?? null

  const pickBranch = (id: string) => {
    if (user) posStorage.writeBranch({ tenantId: user.tenantId }, id)
    setBranchOverride(id)
    setRegisterId(null)
    setLastCompletedSale(null)
  }
  const switchBranch = () => {
    if (user) posStorage.clearBranch({ tenantId: user.tenantId })
    setBranchOverride(null)
    setRegisterId(null)
    setLastCompletedSale(null)
  }

  const registersQuery = useQuery({
    queryKey: ['pos', 'registers', branchId],
    queryFn: () => posApi.registers(branchId ?? undefined),
    enabled: !!branchId,
  })
  const registers = registersQuery.data ?? []

  const sessionQuery = useQuery({
    queryKey: ['session', 'current', registerId],
    queryFn: () => sessionsApi.current({ registerId: registerId! }),
    enabled: !!registerId,
    retry: false,
  })

  // Fallback source for "current sale": the most recent sale in THIS register session (bounded by
  // its openedAtUtc, so a closed-then-reopened register never leaks a stale sale from a past
  // shift). Covers refresh / Exit -> Continue, where the in-memory lastCompletedSale is gone even
  // though the cashier really has completed sales this session. invalidateQueries(['sales']) on
  // checkout success already covers this key too (prefix match), so it refreshes automatically.
  const openedAtUtc = sessionQuery.data?.openedAtUtc
  const recentSalesQuery = useQuery({
    queryKey: ['sales', 'recent', registerId, openedAtUtc],
    // No status filter — a voided sale still IS the most recent transaction in this session and
    // should keep showing as such (labeled voided), not vanish. invalidateQueries(['sales']) on
    // both checkout and void success already covers this key (prefix match), so it stays fresh.
    queryFn: () => salesApi.list({ registerId: registerId!, fromUtc: openedAtUtc, pageSize: 1 }),
    enabled: !!registerId && !!openedAtUtc,
  })

  if (!canOperate) return <PosDenied />

  if (contextQuery.isPending) return <PosCentered><LoadingState /></PosCentered>
  if (contextQuery.isError) {
    return (
      <PosCentered>
        <ErrorState
          message={(contextQuery.error as Error).message}
          onRetry={() => contextQuery.refetch()}
        />
      </PosCentered>
    )
  }

  if (ctx!.canPickBranch && !branchId) {
    return (
      <PosCentered>
        <BranchPicker branches={ctx!.branches} onPick={pickBranch} />
      </PosCentered>
    )
  }

  if (!branchId) {
    return (
      <PosCentered>
        <ErrorState message="No active branch is available for the point of sale." />
      </PosCentered>
    )
  }

  if (registersQuery.isPending) return <PosCentered><LoadingState /></PosCentered>

  const chosen = registerId ? registers.find((r) => r.id === registerId) : undefined
  const backToRegisters = () => {
    setRegisterId(null)
    setSkipGate(false)
    setLastCompletedSale(null)
    registersQuery.refetch()
  }

  const sessionErr = sessionQuery.error
  // A register grabbed by someone else since the picker loaded → show the picker again with a notice.
  const takenByAnother = sessionErr instanceof ApiError && sessionErr.status === 403

  if (!registerId || !chosen || takenByAnother) {
    return (
      <PosCentered>
        <RegisterPicker
          registers={registers}
          branchName={branchName}
          notice={takenByAnother ? 'That register is now in use by someone else.' : pickerNotice}
          onSelect={(id) => {
            setPickerNotice(null)
            setSkipGate(false)
            setRegisterId(id)
          }}
          onContinue={(id) => {
            setPickerNotice(null)
            setSkipGate(true)
            setRegisterId(id)
          }}
          onSwitchBranch={ctx!.canPickBranch ? switchBranch : undefined}
        />
      </PosCentered>
    )
  }

  if (sessionQuery.isError) {
    const err = sessionQuery.error
    if (err instanceof ApiError && err.status === 404) {
      return (
        <PosCentered>
          <PosSessionGate
            register={chosen}
            onOpened={() => sessionQuery.refetch()}
            onSwitchRegister={backToRegisters}
          />
        </PosCentered>
      )
    }
    return (
      <PosCentered>
        <ErrorState message={(err as Error).message} onRetry={() => sessionQuery.refetch()} />
      </PosCentered>
    )
  }

  if (sessionQuery.isPending || !sessionQuery.data) {
    if (skipGate && sessionQuery.isFetched && !sessionQuery.data) {
      return (
        <PosCentered>
          <PosSessionGate
            register={chosen}
            onOpened={() => sessionQuery.refetch()}
            onSwitchRegister={backToRegisters}
          />
        </PosCentered>
      )
    }
    return <PosCentered><LoadingState /></PosCentered>
  }

  const session = sessionQuery.data

  const fetchedRecent = recentSalesQuery.data?.items[0]
  const currentSale: CurrentSaleRef | null = lastCompletedSale
    ? lastCompletedSale
    : fetchedRecent
      ? { saleId: fetchedRecent.id, saleNumber: fetchedRecent.saleNumber, status: fetchedRecent.status }
      : null

  // Keep showing the same sale once it's voided (status updates in place) rather than hiding it —
  // it's still the most recent thing that happened in this session, just no longer voidable.
  // Never touches the active cart — that's a separate, independent decision.
  const handleSaleVoided = (voided: CurrentSaleRef) => {
    if (currentSale && currentSale.saleId === voided.saleId) setLastCompletedSale(voided)
  }

  return (
    <>
      <PosShell
        session={session}
        register={chosen}
        branchName={branchName}
        branchCode={branchCode}
        onCloseSession={() => setCloseOpen(true)}
        onCashIn={() => setCashMovementType('CashIn')}
        onCashOut={() => setCashMovementType('CashOut')}
      >
        <PosTerminal
          tenantId={user!.tenantId}
          branchId={branchId}
          registerId={chosen.id}
          registerSessionId={session.id}
          currentSale={currentSale}
          onSaleCompleted={setLastCompletedSale}
          onSaleVoided={handleSaleVoided}
          onSessionLost={() => sessionQuery.refetch()}
        />
      </PosShell>

      <CloseSessionModal
        open={closeOpen}
        onClose={() => setCloseOpen(false)}
        session={session}
        onClosed={() => {
          setCloseOpen(false)
          backToRegisters()
        }}
      />

      <CashMovementModal
        open={cashMovementType !== null}
        onClose={() => setCashMovementType(null)}
        sessionId={session.id}
        type={cashMovementType ?? 'CashIn'}
        onDone={() => sessionQuery.refetch()}
      />
    </>
  )
}
