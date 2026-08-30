import { apiRequest } from './client'
import { qs } from './query-string'
import type {
  CategoryDto,
  CategoryListParams,
  CreateCategoryRequest,
  UpdateCategoryRequest,
  CreateProductRequest,
  UpdateProductRequest,
  PagedResult,
  ProductDetailDto,
  ProductDto,
  ProductListParams,
  ProductVariantDto,
  VariantInput,
} from './types'

/** Stopgap page size for the "all active categories" picker feed — no typeahead endpoint exists yet. */
export const ACTIVE_CATEGORY_FETCH_LIMIT = 100

export const categoriesApi = {
  list: (params: CategoryListParams) =>
    apiRequest<PagedResult<CategoryDto>>(`/api/categories${qs({ ...params })}`),

  listAllActive: async (): Promise<CategoryDto[]> => {
    const page = await apiRequest<PagedResult<CategoryDto>>(
      `/api/categories${qs({ isActive: true, pageSize: ACTIVE_CATEGORY_FETCH_LIMIT })}`,
    )
    return page.items
  },

  get: (id: string) => apiRequest<CategoryDto>(`/api/categories/${id}`),

  create: (body: CreateCategoryRequest) =>
    apiRequest<CategoryDto>('/api/categories', { method: 'POST', body }),

  update: (id: string, body: UpdateCategoryRequest) =>
    apiRequest<CategoryDto>(`/api/categories/${id}`, { method: 'PUT', body }),

  deactivate: (id: string) =>
    apiRequest<void>(`/api/categories/${id}`, { method: 'DELETE' }),
}

export const productsApi = {
  list: (params: ProductListParams) =>
    apiRequest<PagedResult<ProductDto>>(`/api/products${qs({ ...params })}`),

  get: (id: string) => apiRequest<ProductDetailDto>(`/api/products/${id}`),

  create: (body: CreateProductRequest) =>
    apiRequest<ProductDetailDto>('/api/products', { method: 'POST', body }),

  update: (id: string, body: UpdateProductRequest) =>
    apiRequest<ProductDetailDto>(`/api/products/${id}`, { method: 'PUT', body }),

  deactivate: (id: string) =>
    apiRequest<void>(`/api/products/${id}`, { method: 'DELETE' }),
}

export const variantsApi = {
  list: (productId: string) =>
    apiRequest<ProductVariantDto[]>(`/api/products/${productId}/variants`),

  create: (productId: string, body: VariantInput) =>
    apiRequest<ProductVariantDto>(`/api/products/${productId}/variants`, { method: 'POST', body }),

  update: (productId: string, variantId: string, body: VariantInput) =>
    apiRequest<ProductVariantDto>(`/api/products/${productId}/variants/${variantId}`, {
      method: 'PUT',
      body,
    }),

  deactivate: (productId: string, variantId: string) =>
    apiRequest<void>(`/api/products/${productId}/variants/${variantId}`, { method: 'DELETE' }),
}
