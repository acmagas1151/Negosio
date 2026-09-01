using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Application.Inventory;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Pos;

public sealed class CheckoutService : ICheckoutService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<CheckoutRequest> _validator;
    private readonly IDocumentNumberService _documentNumbers;
    private readonly IInventoryPosting _inventory;
    private readonly IBranchAccessResolver _branchAccess;
    private readonly ILogger<CheckoutService> _logger;

    public CheckoutService(
        ITenantDbContext db,
        ICurrentUser currentUser,
        IValidator<CheckoutRequest> validator,
        IDocumentNumberService documentNumbers,
        IInventoryPosting inventory,
        IBranchAccessResolver branchAccess,
        ILogger<CheckoutService> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _validator = validator;
        _documentNumbers = documentNumbers;
        _inventory = inventory;
        _branchAccess = branchAccess;
        _logger = logger;
    }

    private sealed record ResolvedLine(
        Guid ProductVariantId, decimal Quantity, DiscountType DiscountType, decimal DiscountValue,
        string ProductNameSnapshot, string? VariantNameSnapshot, string? SkuSnapshot, string? BarcodeSnapshot,
        decimal UnitPrice, decimal CostPrice, bool TrackInventory, SaleLineCalculator.Line Amounts);

    public async Task<SaleResultDto> CheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _validator.ValidateAndThrowAppAsync(request, cancellationToken);

        // 1. Idempotency pre-check: a repeat of the same request returns the same sale.
        var existing = await _db.Sales.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.ClientRequestId == request.ClientRequestId, cancellationToken);
        if (existing is not null)
        {
            return ToResult(existing, wasExisting: true);
        }

        // 2. Validate branch + register session.
        var branchId = await _branchAccess.ResolveTargetBranchAsync(request.BranchId, cancellationToken: cancellationToken);
        var branch = await _db.Branches
            .SingleAsync(b => b.TenantId == tenantId && b.Id == branchId, cancellationToken);

        var session = await _db.RegisterSessions
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == request.RegisterSessionId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "Register session not found.");

        if (session.BranchId != branch.Id)
        {
            throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "Register session does not belong to this branch.");
        }

        if (session.Status != RegisterSessionStatus.Open)
        {
            throw new BusinessRuleException(ErrorCodes.RegisterSessionNotOpen, "The register session is not open.");
        }

        var tenant = await _db.TenantProfiles.SingleAsync(p => p.Id == tenantId, cancellationToken);

        // 3. Merge duplicate variant lines (first non-None discount wins for the merged line).
        var merged = request.Items
            .GroupBy(i => i.ProductVariantId)
            .Select(g =>
            {
                var discount = g.Select(x => x.Discount).FirstOrDefault(d => d is { Type: not DiscountType.None })
                               ?? new CheckoutDiscountInput();
                return new CheckoutItemInput(g.Key, g.Sum(x => x.Quantity), discount);
            })
            .ToList();

        // 4. Load + validate variants/products, then compute every line server-side.
        var variantIds = merged.Select(m => m.ProductVariantId).ToList();
        var variants = await _db.ProductVariants
            .Where(v => v.TenantId == tenantId && variantIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, cancellationToken);
        var products = await _db.Products
            .Where(p => p.TenantId == tenantId && variants.Values.Select(v => v.ProductId).Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        var lines = new List<ResolvedLine>(merged.Count);
        foreach (var item in merged)
        {
            if (!variants.TryGetValue(item.ProductVariantId, out var variant) || !variant.IsActive)
            {
                throw new BusinessRuleException(ErrorCodes.InvalidSaleItem, "A sale item refers to an unknown or inactive variant.");
            }

            if (!products.TryGetValue(variant.ProductId, out var product) || !product.IsActive)
            {
                throw new BusinessRuleException(ErrorCodes.InvalidSaleItem, "A sale item refers to an inactive product.");
            }

            if (item.Quantity <= 0m)
            {
                throw new BusinessRuleException(ErrorCodes.InvalidQuantity, "Item quantity must be greater than zero.");
            }

            var discount = item.Discount ?? new CheckoutDiscountInput();
            var amounts = SaleLineCalculator.Calculate(
                variant.SellingPrice, item.Quantity, discount.Type, discount.Value,
                tenant.TaxRatePercent, tenant.PricesIncludeTax);

            lines.Add(new ResolvedLine(
                variant.Id, item.Quantity, discount.Type, discount.Value,
                product.Name, variant.IsDefault ? null : variant.Name, variant.Sku, variant.Barcode,
                variant.SellingPrice, variant.CostPrice, product.TrackInventory, amounts));
        }

        var subtotal = Money.Round(lines.Sum(l => l.Amounts.Gross));
        var discountTotal = Money.Round(lines.Sum(l => l.Amounts.Discount));
        var taxTotal = Money.Round(lines.Sum(l => l.Amounts.Tax));
        var grandTotal = tenant.PricesIncludeTax
            ? subtotal - discountTotal
            : subtotal - discountTotal + taxTotal;

        // 5. Resolve payments against the grand total.
        var payments = ResolvePayments(request.Payments, grandTotal);

        // 6-10. One transaction: number, sale, items, payments, inventory, movements.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var saleNumber = await _documentNumbers.NextAsync(tenantId, branch.Id, DocumentNumberType.Sale, branch.Code, cancellationToken);

        var sale = Sale.Begin(tenantId, branch.Id, session.Id, saleNumber, request.ClientRequestId, _currentUser.UserId);
        foreach (var line in lines)
        {
            sale.AddItem(
                line.ProductVariantId, line.ProductNameSnapshot, line.VariantNameSnapshot, line.SkuSnapshot, line.BarcodeSnapshot,
                line.UnitPrice, line.Quantity, line.DiscountType, line.DiscountValue,
                line.Amounts.Gross, line.Amounts.Discount, line.Amounts.Tax, line.Amounts.Net, line.CostPrice);
        }

        foreach (var p in payments)
        {
            sale.AddPayment(p.Method, p.Amount, p.ReferenceNumber, p.ReceivedAmount, p.ChangeAmount);
        }

        sale.Complete(subtotal, discountTotal, taxTotal, grandTotal, payments.Sum(p => p.Amount), Money.Round(payments.Sum(p => p.ChangeAmount ?? 0m)));
        _db.Sales.Add(sale);

        try
        {
            // Idempotency gate: a concurrent duplicate loses the race on (TenantId, ClientRequestId).
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (SqlUniqueViolation.TryGetConstraintName(ex, out var name))
        {
            await transaction.RollbackAsync(cancellationToken);

            if (name?.Contains("ClientRequestId", StringComparison.OrdinalIgnoreCase) == true)
            {
                var winner = await _db.Sales.AsNoTracking()
                    .FirstAsync(s => s.TenantId == tenantId && s.ClientRequestId == request.ClientRequestId, cancellationToken);
                return ToResult(winner, wasExisting: true);
            }

            throw new ConflictException(ErrorCodes.CheckoutConcurrencyConflict, "The sale could not be recorded. Please retry.");
        }

        foreach (var line in lines.Where(l => l.TrackInventory))
        {
            await _inventory.DeductForSaleAsync(
                tenantId, branch.Id, line.ProductVariantId, line.Quantity, sale.Id, _currentUser.UserId, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Checkout completed: Sale {SaleId} {SaleNumber} tenant {TenantId} branch {BranchId} session {SessionId} user {UserId} clientRequest {ClientRequestId} total {GrandTotal}",
            sale.Id, sale.SaleNumber, tenantId, branch.Id, session.Id, _currentUser.UserId, request.ClientRequestId, grandTotal);

        return ToResult(sale, wasExisting: false);
    }

    private sealed record ResolvedPayment(PaymentMethod Method, decimal Amount, decimal? ReceivedAmount, decimal? ChangeAmount, string? ReferenceNumber);

    private static List<ResolvedPayment> ResolvePayments(IReadOnlyList<CheckoutPaymentInput> inputs, decimal grandTotal)
    {
        var outstanding = grandTotal;
        var result = new List<ResolvedPayment>(inputs.Count);

        foreach (var input in inputs)
        {
            if (input.Method == PaymentMethod.Cash)
            {
                var received = input.ReceivedAmount ?? input.Amount
                    ?? throw new BusinessRuleException(ErrorCodes.InvalidPayment, "A cash payment needs the amount received.");
                if (received <= 0m)
                {
                    throw new BusinessRuleException(ErrorCodes.InvalidPayment, "A cash payment must be greater than zero.");
                }

                var applied = Money.Round(Math.Min(received, Math.Max(outstanding, 0m)));
                outstanding -= applied;
                result.Add(new ResolvedPayment(PaymentMethod.Cash, applied, Money.Round(received), Money.Round(received - applied), input.ReferenceNumber));
            }
            else
            {
                var amount = input.Amount ?? input.ReceivedAmount
                    ?? throw new BusinessRuleException(ErrorCodes.InvalidPayment, "A non-cash payment needs an amount.");
                if (amount <= 0m)
                {
                    throw new BusinessRuleException(ErrorCodes.InvalidPayment, "A payment must be greater than zero.");
                }

                var applied = Money.Round(Math.Min(amount, Math.Max(outstanding, 0m)));
                outstanding -= applied;
                result.Add(new ResolvedPayment(input.Method, applied, null, null, input.ReferenceNumber));
            }
        }

        if (Money.Round(outstanding) > 0m)
        {
            throw new BusinessRuleException(ErrorCodes.PaymentInsufficient, "The payments do not cover the sale total.");
        }

        return result;
    }

    private static SaleResultDto ToResult(Sale sale, bool wasExisting) => new(
        sale.Id, sale.SaleNumber, sale.Status, sale.Subtotal, sale.DiscountTotal, sale.TaxTotal,
        sale.GrandTotal, sale.AmountPaid, sale.ChangeDue, wasExisting);

    private Guid RequireTenant()
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }

        return _currentUser.TenantId;
    }
}
