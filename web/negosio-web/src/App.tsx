import type { ReactNode } from 'react'
import { Navigate, Route, Routes, useParams, useSearchParams } from 'react-router-dom'
import { ProtectedRoute } from './auth/ProtectedRoute'
import { RequireCapability } from './auth/RequireCapability'
import CategoriesPage from './pages/CategoriesPage'
import DashboardPage from './pages/DashboardPage'
import DeliveryReceiptPage from './pages/DeliveryReceiptPage'
import DeliveryReportsPage from './pages/DeliveryReportsPage'
import InventoryPage from './pages/InventoryPage'
import InviteAcceptPage from './pages/InviteAcceptPage'
import LoginPage from './pages/LoginPage'
import MovementsPage from './pages/MovementsPage'
import PosCompletePage from './pages/PosCompletePage'
import PosPage from './pages/PosPage'
import ProductCreatePage from './pages/ProductCreatePage'
import ProductDetailPage from './pages/ProductDetailPage'
import ProductEditPage from './pages/ProductEditPage'
import ProductsPage from './pages/ProductsPage'
import ReceiptPage from './pages/ReceiptPage'
import RegisterPage from './pages/RegisterPage'
import RegistersPage from './pages/RegistersPage'
import ReportsPage from './pages/ReportsPage'
import SaleDetailPage from './pages/SaleDetailPage'
import SalesPage from './pages/SalesPage'
import BranchesPage from './pages/BranchesPage'
import SettingsPage from './pages/SettingsPage'
import ReceiptSettingsPage from './pages/settings/ReceiptSettingsPage'
import StaffPage from './pages/StaffPage'

/** Forwards the friendlier /pickups/:id alias to the canonical /delivery-receipts/:id route,
 * preserving the id and any query string (e.g. ?print=1). */
function PickupRedirect() {
  const { id = '' } = useParams()
  const [params] = useSearchParams()
  const qs = params.toString()
  return <Navigate to={`/delivery-receipts/${id}${qs ? `?${qs}` : ''}`} replace />
}

const protectedRoutes: Array<{ path: string; element: ReactNode }> = [
  { path: '/dashboard', element: <DashboardPage /> },
  { path: '/products', element: <ProductsPage /> },
  { path: '/products/new', element: <ProductCreatePage /> },
  { path: '/products/:id', element: <ProductDetailPage /> },
  { path: '/products/:id/edit', element: <ProductEditPage /> },
  { path: '/categories', element: <CategoriesPage /> },
  { path: '/inventory', element: <InventoryPage /> },
  { path: '/inventory/movements', element: <MovementsPage /> },
  {
    path: '/registers',
    element: (
      <RequireCapability capability="register:manage" title="Registers">
        <RegistersPage />
      </RequireCapability>
    ),
  },
  {
    path: '/sales',
    element: (
      <RequireCapability capability="sales:view" title="Sales">
        <SalesPage />
      </RequireCapability>
    ),
  },
  {
    path: '/sales/:id',
    element: (
      <RequireCapability capability="sales:view" title="Sale">
        <SaleDetailPage />
      </RequireCapability>
    ),
  },
  { path: '/sales/:id/receipt', element: <ReceiptPage /> },
  { path: '/delivery-receipts/:id', element: <DeliveryReceiptPage /> },
  // Friendlier alias for pickups — the canonical, linked-from-everywhere path stays
  // /delivery-receipts/:id (see task-17-brief.md Step 2); this just forwards the id.
  { path: '/pickups/:id', element: <PickupRedirect /> },
  {
    path: '/reports',
    element: (
      <RequireCapability capability="reports:view" title="Reports">
        <ReportsPage />
      </RequireCapability>
    ),
  },
  {
    path: '/reports/delivery',
    element: (
      <RequireCapability capability="reports:view" title="Delivery Reports">
        <DeliveryReportsPage />
      </RequireCapability>
    ),
  },
  { path: '/settings', element: <Navigate to="/settings/tax" replace /> },
  { path: '/settings/tax', element: <SettingsPage /> },
  {
    path: '/settings/receipts',
    element: (
      <RequireCapability capability="receipt:settings" title="Receipt settings">
        <ReceiptSettingsPage />
      </RequireCapability>
    ),
  },
  {
    path: '/staff',
    element: (
      <RequireCapability capability={['staff:manage', 'staff:permissions']} title="Staff">
        <StaffPage />
      </RequireCapability>
    ),
  },
  {
    path: '/branches',
    element: (
      <RequireCapability capability="branch:manage" title="Branches">
        <BranchesPage />
      </RequireCapability>
    ),
  },
]

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<Navigate to="/dashboard" replace />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/login" element={<LoginPage />} />
      <Route path="/invite/:token" element={<InviteAcceptPage />} />
      {protectedRoutes.map(({ path, element }) => (
        <Route key={path} path={path} element={<ProtectedRoute>{element}</ProtectedRoute>} />
      ))}
      <Route path="/pos" element={<ProtectedRoute><PosPage /></ProtectedRoute>} />
      <Route
        path="/pos/complete/:saleId"
        element={<ProtectedRoute><PosCompletePage /></ProtectedRoute>}
      />
      <Route path="*" element={<Navigate to="/dashboard" replace />} />
    </Routes>
  )
}
