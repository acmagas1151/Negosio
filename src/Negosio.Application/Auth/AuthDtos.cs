using Negosio.Domain.Enums;

namespace Negosio.Application.Auth;

public sealed record RegisterBranchInput(
    string Name,
    string Code,
    string AddressLine1,
    string? AddressLine2,
    string City,
    string Province,
    string? PostalCode);

public sealed record RegisterOwnerInput(
    string FirstName,
    string LastName,
    string Email,
    string Password);

public sealed record RegisterRequest(
    string BusinessName,
    string BusinessType,
    RegisterBranchInput Branch,
    RegisterOwnerInput Owner);

public sealed record LoginRequest(string Email, string Password);

public sealed record AuthUserDto(
    Guid Id,
    Guid TenantId,
    string TenantName,
    BusinessType BusinessType,
    string FirstName,
    string LastName,
    string Email,
    UserRole Role);

public sealed record LoginResponse(
    string AccessToken,
    DateTime ExpiresAtUtc,
    AuthUserDto User);

public sealed record RegisterResponse(
    Guid TenantId,
    Guid BranchId,
    Guid OwnerUserId);
