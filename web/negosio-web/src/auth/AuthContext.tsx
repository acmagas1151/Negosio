import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { ApiError, setAuthToken, setUnauthorizedHandler } from '../api/client'
import { authApi } from '../api/endpoints'
import type { AuthUser, LoginRequest } from '../api/types'
import { tokenStorage } from './storage'

interface AuthContextValue {
  user: AuthUser | null
  isAuthenticated: boolean
  /** True while we are validating a persisted token on first load. */
  isInitializing: boolean
  login: (credentials: LoginRequest) => Promise<void>
  logout: () => void
}

const AuthContext = createContext<AuthContextValue | undefined>(undefined)

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const [user, setUser] = useState<AuthUser | null>(null)
  // Only "initializing" if there is a persisted token we still need to validate.
  const [isInitializing, setIsInitializing] = useState(() => tokenStorage.get() !== null)

  const logout = useCallback(() => {
    tokenStorage.clear()
    setAuthToken(null)
    setUser(null)
    queryClient.clear()
  }, [queryClient])

  // Let the API client trigger a logout when any request comes back 401.
  useEffect(() => {
    setUnauthorizedHandler(logout)
    return () => setUnauthorizedHandler(null)
  }, [logout])

  // On first load, if we have a persisted token, verify it by loading the profile.
  useEffect(() => {
    const token = tokenStorage.get()
    if (!token) return

    let cancelled = false
    setAuthToken(token)
    authApi
      .me()
      .then((profile) => {
        if (!cancelled) setUser(profile)
      })
      .catch((error) => {
        if (!cancelled && error instanceof ApiError && error.status === 401) logout()
      })
      .finally(() => {
        if (!cancelled) setIsInitializing(false)
      })

    return () => {
      cancelled = true
    }
  }, [logout])

  const login = useCallback(
    async (credentials: LoginRequest) => {
      const result = await authApi.login(credentials)
      tokenStorage.set(result.accessToken)
      setAuthToken(result.accessToken)
      setUser(result.user)
    },
    [],
  )

  const value = useMemo<AuthContextValue>(
    () => ({ user, isAuthenticated: !!user, isInitializing, login, logout }),
    [user, isInitializing, login, logout],
  )

  return <AuthContext value={value}>{children}</AuthContext>
}

// eslint-disable-next-line react-refresh/only-export-components
export function useAuth() {
  const context = useContext(AuthContext)
  if (!context) throw new Error('useAuth must be used within an AuthProvider')
  return context
}
