import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { staffApi } from '../../api/staff'
import type { StaffMemberDto } from '../../api/types'
import { useToast } from '../ui'

interface Props {
  member: StaffMemberDto
}

/** Inline toggle for a Cashier row's "Allow voiding completed sales" permission — deliberately not
 * a modal, per the phase's scope: this is the only editable field, not a generic permissions editor. */
export function SalesVoidPermissionToggle({ member }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()

  const mutation = useMutation({
    mutationFn: (next: boolean) => staffApi.setSalesVoidPermission(member.id, { salesVoid: next }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['staff'] })
      toast('success', 'Permission updated')
    },
    onError: (err) => toast('error', err instanceof ApiError ? err.message : 'Could not update this permission.'),
  })

  return (
    <label className="flex items-center gap-1.5 text-[13px] text-text-secondary">
      <input
        type="checkbox"
        checked={member.salesVoid}
        disabled={mutation.isPending}
        onChange={(e) => mutation.mutate(e.target.checked)}
        className="size-4 rounded border-border-strong text-primary-600 focus-visible:outline-2 focus-visible:outline-primary-500"
      />
      Allow void
    </label>
  )
}
