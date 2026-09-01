import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate } from 'react-router-dom'
import { branchesApi } from '../api/inventory'
import { ApiError } from '../api/client'
import { registersApi, sessionsApi } from '../api/pos'
import { useAuth } from '../auth/AuthContext'
import { useCan } from '../lib/useCan'
import { posStorage } from '../lib/posStorage'
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

  const branchesQuery = useQuery({ queryKey: ['branches'], queryFn: () => branchesApi.list() })
  const branch = branchesQuery.data?.[0]

  const registersQuery = useQuery({
    queryKey: ['registers', 'active-for-pos'],
    queryFn: () => registersApi.list({ isActive: true, pageSize: 100 }),
    enabled: !!branch,
  })
  const activeRegisters = registersQuery.data?.items ?? []

  // Explicit user choice only; `null` = fall back to derivation.
  const [override, setOverride] = useState<string | null>(null)
  // The user asked to re-pick: suppress the persisted / sole-register fallback until they choose.
  const [forcePicker, setForcePicker] = useState(false)
  const [closeOpen, setCloseOpen] = useState(false)

  const persisted =
    branch && user
      ? posStorage.readRegister({ tenantId: user.tenantId, branchId: branch.id })
      : null
  const persistedValid =
    persisted && activeRegisters.some((r) => r.id === persisted) ? persisted : null

  const registerId: string | null = forcePicker
    ? override
    : (override ?? persistedValid ?? (activeRegisters.length === 1 ? activeRegisters[0].id : null))

  const pickRegister = (id: string) => {
    if (branch && user) posStorage.writeRegister({ tenantId: user.tenantId, branchId: branch.id }, id)
    setOverride(id)
    setForcePicker(false)
  }
  const askForDifferentRegister = () => {
    setOverride(null)
    setForcePicker(true)
  }

  const sessionQuery = useQuery({
    queryKey: ['session', 'current', registerId],
    queryFn: () => sessionsApi.current({ registerId: registerId! }),
    enabled: !!registerId,
    retry: false,
  })

  if (!canOperate) return <PosDenied />
  if (branchesQuery.isPending || (!!branch && registersQuery.isPending)) {
    return <PosCentered><LoadingState /></PosCentered>
  }
  if (!branch) {
    return (
      <PosCentered>
        <ErrorState message="No branch is configured for this business." />
      </PosCentered>
    )
  }

  if (!registerId) {
    return (
      <PosCentered>
        <RegisterPicker registers={activeRegisters} onPick={pickRegister} />
      </PosCentered>
    )
  }

  const register = activeRegisters.find((r) => r.id === registerId)
  if (!register) {
    return (
      <PosCentered>
        <RegisterPicker registers={activeRegisters} onPick={pickRegister} />
      </PosCentered>
    )
  }

  if (sessionQuery.isError) {
    const err = sessionQuery.error
    if (err instanceof ApiError && err.status === 404) {
      return (
        <PosCentered>
          <PosSessionGate
            register={register}
            onOpened={() => sessionQuery.refetch()}
            onSwitchRegister={askForDifferentRegister}
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
    return <PosCentered><LoadingState /></PosCentered>
  }

  const session = sessionQuery.data

  return (
    <>
      <PosShell
        session={session}
        register={register}
        onCloseSession={() => setCloseOpen(true)}
      >
        <PosTerminal
          tenantId={user!.tenantId}
          branchId={branch.id}
          registerId={register.id}
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
        onClosed={() => sessionQuery.refetch()}
      />
    </>
  )
}
