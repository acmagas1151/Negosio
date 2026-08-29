import { apiRequest } from './client'
import type {
  AuthUser,
  DashboardResponse,
  LoginRequest,
  LoginResponse,
  RegisterRequest,
  RegisterResponse,
} from './types'

export const authApi = {
  register: (body: RegisterRequest) =>
    apiRequest<RegisterResponse>('/api/auth/register', { method: 'POST', body, signOutOn401: false }),

  login: (body: LoginRequest) =>
    apiRequest<LoginResponse>('/api/auth/login', { method: 'POST', body, signOutOn401: false }),

  me: () => apiRequest<AuthUser>('/api/auth/me'),
}

export const dashboardApi = {
  get: () => apiRequest<DashboardResponse>('/api/dashboard'),
}
