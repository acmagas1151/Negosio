import { apiRequest } from './client'
import type {
  AcceptInvitationRequest,
  AcceptInvitationResultDto,
  ChangeStaffBranchRequest,
  ChangeStaffRoleRequest,
  InvitationPreviewDto,
  InviteStaffRequest,
  StaffInvitationResultDto,
  StaffMemberDto,
} from './types'

/** Authenticated staff administration (Owner / Admin — the backend enforces StaffManage). */
export const staffApi = {
  list: () => apiRequest<StaffMemberDto[]>('/api/staff'),

  invite: (body: InviteStaffRequest) =>
    apiRequest<StaffInvitationResultDto>('/api/staff/invitations', { method: 'POST', body }),

  resendInvitation: (id: string) =>
    apiRequest<StaffInvitationResultDto>(`/api/staff/invitations/${id}/resend`, { method: 'POST' }),

  revokeInvitation: (id: string) =>
    apiRequest<void>(`/api/staff/invitations/${id}`, { method: 'DELETE' }),

  changeRole: (id: string, body: ChangeStaffRoleRequest) =>
    apiRequest<StaffMemberDto>(`/api/staff/${id}/role`, { method: 'PUT', body }),

  changeBranch: (id: string, body: ChangeStaffBranchRequest) =>
    apiRequest<StaffMemberDto>(`/api/staff/${id}/branch`, { method: 'POST', body }),

  deactivate: (id: string) =>
    apiRequest<StaffMemberDto>(`/api/staff/${id}/deactivate`, { method: 'POST' }),

  reactivate: (id: string) =>
    apiRequest<StaffMemberDto>(`/api/staff/${id}/reactivate`, { method: 'POST' }),
}

/** Public invitation acceptance — hits /api/auth/invitations/* and needs no bearer token. */
export const invitationsApi = {
  preview: (token: string) =>
    apiRequest<InvitationPreviewDto>(`/api/auth/invitations/${encodeURIComponent(token)}`, {
      signOutOn401: false,
    }),

  accept: (token: string, body: AcceptInvitationRequest) =>
    apiRequest<AcceptInvitationResultDto>(
      `/api/auth/invitations/${encodeURIComponent(token)}/accept`,
      { method: 'POST', body, signOutOn401: false },
    ),
}
