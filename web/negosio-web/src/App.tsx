import type { ReactNode } from 'react'
import { Navigate, Route, Routes } from 'react-router-dom'
import { ProtectedRoute } from './auth/ProtectedRoute'
import CategoriesPage from './pages/CategoriesPage'
import DashboardPage from './pages/DashboardPage'
import InventoryPage from './pages/InventoryPage'
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
import SaleDetailPage from './pages/SaleDetailPage'
import SalesPage from './pages/SalesPage'
import SettingsPage from './pages/SettingsPage'

const protectedRoutes: Array<{ path: string; element: ReactNode }> = [
  { path: '/dashboard', element: <DashboardPage /> },
  { path: '/products', element: <ProductsPage /> },
  { path: '/products/new', element: <ProductCreatePage /> },
  { path: '/products/:id', element: <ProductDetailPage /> },
  { path: '/products/:id/edit', element: <ProductEditPage /> },
  { path: '/categories', element: <CategoriesPage /> },
  { path: '/inventory', element: <InventoryPage /> },
  { path: '/inventory/movements', element: <MovementsPage /> },
  { path: '/registers', element: <RegistersPage /> },
  { path: '/sales', element: <SalesPage /> },
  { path: '/sales/:id', element: <SaleDetailPage /> },
  { path: '/sales/:id/receipt', element: <ReceiptPage /> },
  { path: '/settings', element: <SettingsPage /> },
]

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<Navigate to="/dashboard" replace />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/login" element={<LoginPage />} />
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
