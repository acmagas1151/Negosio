import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import type { RegisterDto } from '../../api/types'
import { useAuth } from '../../auth/AuthContext'
import { formatMoney } from '../../lib/format'
import { useCan } from '../../lib/useCan'
import { Button } from '../ui'
import { CloseSessionModal } from '../pos/CloseSessionModal'
import { OpenSessionModal } from '../pos/OpenSessionModal'

/** Management-view session state for one register, with open / close / force-close actions. */
export function RegisterSessionCell({ register }: { register: RegisterDto }) {
  const { user } = useAuth()
  const qc = useQueryClient()
  const canOperate = useCan('pos:operate')
  const canForceClose = useCan('register:force-close')

  const [openOpen, setOpenOpen] = useState(false)
  const [closeOpen, setCloseOpen] = useState(false)

  const refresh = () => qc.invalidateQueries({ queryKey: ['registers'] })
  const session = register.openSession
  const mine = session?.openedByUserId === user?.id

  if (!session) {
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
          onOpened={refresh}
        />
      </div>
    )
  }

  const since = new Date(session.openedAtUtc).toLocaleTimeString([], {
    hour: 'numeric',
    minute: '2-digit',
  })

  return (
    <div className="flex flex-wrap items-center gap-2">
      <span className="text-text-secondary">
        <span className="font-semibold text-success-strong">Open</span>
        {' · '}
        {mine ? 'by you' : `by ${session.openedByName}`}
        {' · '}
        {formatMoney(session.openingCash)} · since {since}
      </span>

      {mine ? (
        <Button variant="ghost" size="sm" onClick={() => setCloseOpen(true)}>
          Close
        </Button>
      ) : canForceClose ? (
        <Button variant="ghost" size="sm" onClick={() => setCloseOpen(true)}>
          Force close
        </Button>
      ) : null}

      <CloseSessionModal
        open={closeOpen}
        onClose={() => setCloseOpen(false)}
        variant={mine ? 'self' : 'force'}
        openedByName={session.openedByName}
        session={{
          id: session.sessionId,
          registerName: register.name,
          openingCash: session.openingCash,
        }}
        onClosed={refresh}
      />
    </div>
  )
}
