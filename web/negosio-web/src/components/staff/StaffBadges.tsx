import type { StaffMemberStatus, UserRole } from '../../api/types'
import { roleLabel } from '../../lib/roles'
import { Badge } from '../ui'

export function RoleBadge({ role }: { role: UserRole }) {
  return <Badge tone={role === 'Owner' ? 'purple' : 'neutral'}>{roleLabel(role)}</Badge>
}

const STATUS: Record<StaffMemberStatus, { label: string; tone: 'success' | 'neutral' | 'blue' | 'warning' }> = {
  Active: { label: 'Active', tone: 'success' },
  Deactivated: { label: 'Deactivated', tone: 'neutral' },
  Invited: { label: 'Invited', tone: 'blue' },
  Expired: { label: 'Expired', tone: 'warning' },
}

export function StaffStatusBadge({ status }: { status: StaffMemberStatus }) {
  const s = STATUS[status]
  return <Badge tone={s.tone}>{s.label}</Badge>
}
