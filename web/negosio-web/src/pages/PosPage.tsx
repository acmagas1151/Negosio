import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate } from 'react-router-dom'
import { ApiError } from '../api/client'
import { posApi, sessionsApi } from '../api/pos'
import { useAuth } from '../auth/AuthContext'
import { useCan } from '../lib/useCan'
import { posStorage } from '../lib/posStorage'
import { BranchPicker } from '../components/pos/BranchPicker'
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
  const navigate = useNavigate()
  const canOperate = useCan('pos:operate')

  const contextQuery = useQuery({ queryKey: ['pos', 'context'], queryFn: posApi.context })

  const [branchOverride, setBranchOverride] = useState<string | null>(null)
  const [registerId, setRegisterId] = useState<string | null>(null)
  const [skipGate, setSkipGate] = useState(false)
  const [closeOpen, setCloseOpen] = useState(false)
  const [pickerNotice, setPickerNotice] = useState<string | null>(null)

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

  const pickBranch = (id: string) => {
    if (user) posStorage.writeBranch({ tenantId: user.tenantId }, id)
    setBranchOverride(id)
    setRegisterId(null)
  }
  const switchBranch = () => {
    if (user) posStorage.clearBranch({ tenantId: user.tenantId })
    setBranchOverride(null)
    setRegisterId(null)
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

  return (
    <>
      <PosShell session={session} register={chosen} onCloseSession={() => setCloseOpen(true)}>
        <PosTerminal
          tenantId={user!.tenantId}
          branchId={branchId}
          registerId={chosen.id}
          registerSessionId={session.id}
          onCheckoutSuccess={(result) =>
            navigate(`/pos/complete/${result.saleId}`, { state: { result } })
          }
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
    </>
  )
}
