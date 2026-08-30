import type { ProductDto, UpdateProductRequest } from '../api/types'

type ProductPatch = Partial<
  Pick<
    UpdateProductRequest,
    | 'categoryId'
    | 'name'
    | 'description'
    | 'trackInventory'
    | 'isActive'
    | 'sku'
    | 'barcode'
    | 'costPrice'
    | 'sellingPrice'
  >
>

/**
 * The ONE place an UpdateProductRequest is built — used by ProductEditForm AND by
 * deactivate / reactivate on ProductDetailPage.
 *
 * For a VARIANT product (`product.hasVariants`), sku/barcode/costPrice/sellingPrice are forced to
 * inert values and any such keys in `patch` are ignored. Verified in
 * src/Negosio.Application/Catalog/ProductService.cs (UpdateAsync): those four fields are only read
 * inside `if (!product.HasVariants)`, and UpdateProductRequestValidator accepts costPrice/sellingPrice = 0.
 */
export function buildUpdateProductRequest(product: ProductDto, patch: ProductPatch): UpdateProductRequest {
  const base = {
    categoryId: patch.categoryId ?? product.categoryId,
    name: patch.name ?? product.name,
    description: patch.description !== undefined ? patch.description : product.description,
    trackInventory: patch.trackInventory ?? product.trackInventory,
    isActive: patch.isActive ?? product.isActive,
  }

  if (product.hasVariants) {
    return { ...base, sku: null, barcode: null, costPrice: 0, sellingPrice: 0 }
  }

  return {
    ...base,
    sku: patch.sku !== undefined ? patch.sku : product.sku,
    barcode: patch.barcode !== undefined ? patch.barcode : product.barcode,
    costPrice: patch.costPrice ?? product.minCostPrice ?? 0,
    sellingPrice: patch.sellingPrice ?? product.minSellingPrice,
  }
}
