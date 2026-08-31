import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { sessionsApi } from '../../api/pos'
import type { RegisterDto } from '../../api/types'
import { formatMoney } from '../../lib/format'
import { useCan } from '../../lib/useCan'
import { Button, SkeletonText } from '../ui'
import { CloseSessionModal } from '../pos/CloseSessionModal'
import { OpenSessionModal } from '../pos/OpenSessionModal'

/** Live "current session" state for one register, plus open/close actions. */
export function RegisterSessionCell({ register }: { register: RegisterDto }) {
  const canOperate = useCan('pos:operate')
  const [openOpen, setOpenOpen] = useState(false)
  const [closeOpen, setCloseOpen] = useState(false)

  const sessionQuery = useQuery({
    queryKey: ['session', 'current', register.id],
    queryFn: () => sessionsApi.current({ registerId: register.id }),
    retry: false, // a 404 is the normal "no open session" state
  })

  if (sessionQuery.isPending) return <SkeletonText className="w-28" />

  if (sessionQuery.isError) {
    const err = sessionQuery.error
    if (err instanceof ApiError && err.status === 404) {
      return (
        <div className="flex items-center gap-2">
          <span className="text-text-muted">No open session</span>
          {canOperate && (
            <Button variant="ghost" size="sm" onClick={() => setOpenOpen(true)}>
              Open session
            </Button>
          )}
          <OpenSessionModal
            open={openOpen}
            onClose={() => setOpenOpen(false)}
            registerId={register.id}
            registerName={register.name}
            onOpened={() => sessionQuery.refetch()}
          />
        </div>
      )
    }
    return <span className="text-text-muted">Unavailable</span>
  }

  const session = sessionQuery.data
  const since = new Date(session.openedAtUtc).toLocaleTimeString([], {
    hour: 'numeric',
    minute: '2-digit',
  })

  return (
    <div className="flex items-center gap-2">
      <span className="text-text-secondary">
        <span className="font-semibold text-success-strong">Open</span> ·{' '}
        {formatMoney(session.openingCash)} · since {since}
      </span>
      {canOperate && (
        <Button variant="ghost" size="sm" onClick={() => setCloseOpen(true)}>
          Close
        </Button>
      )}
      <CloseSessionModal
        open={closeOpen}
        onClose={() => setCloseOpen(false)}
        session={session}
        onClosed={() => sessionQuery.refetch()}
      />
    </div>
  )
}
