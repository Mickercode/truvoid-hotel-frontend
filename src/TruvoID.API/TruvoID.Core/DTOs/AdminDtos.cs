namespace TruvoID.Core.DTOs;

// ─── Admin Overview ───
public record AdminOverviewDto
{
    public decimal RevenueMtd { get; init; }
    public decimal CostsMtd { get; init; }
    public decimal NetMargin { get; init; }
    public int ActiveInstitutions { get; init; }
    public int PendingInstitutions { get; init; }
    public int TotalApiCallsMtd { get; init; }
    public decimal TotalWalletBalances { get; init; }
    public int PendingTopUpApprovals { get; init; }
    public decimal RevenueGrowthPct { get; init; }
    public int NewInstitutionsThisMonth { get; init; }
    public List<InstitutionVolumeDto> TopInstitutions { get; init; } = [];
    public CallBreakdownDto CallBreakdown { get; init; } = new();
}

public record InstitutionVolumeDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public int CallsMtd { get; init; }
    public decimal RevenueMtd { get; init; }
    public bool Active { get; init; }
}

public record CallBreakdownDto
{
    public int NinCalls { get; init; }
    public int BvnCalls { get; init; }
    public int PhoneCalls { get; init; }
    public int Total => NinCalls + BvnCalls + PhoneCalls;
}

// ─── Admin Institutions ───
public record AdminInstitutionDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public string Status { get; init; } = "Active";
    public decimal WalletBalance { get; init; }
    public decimal Tokens { get; init; }
    public int ApiCallsMtd { get; init; }
    public DateTime JoinedDate { get; init; }
    public string Type { get; init; } = "";
}

// ─── Admin Financials ───
public record AdminFinancialsDto
{
    public decimal GrossRevenue { get; init; }
    public decimal NimcPayouts { get; init; }
    public decimal NetProfit { get; init; }
    public decimal MarginPct { get; init; }
    public int TotalCalls { get; init; }
    public List<AdminTopUpDto> PendingTopUps { get; init; } = [];
    public List<AdminTransactionDto> Transactions { get; init; } = [];
}

public record AdminTopUpDto
{
    public Guid Id { get; init; }
    public string Institution { get; init; } = "";
    public string Email { get; init; } = "";
    public decimal Amount { get; init; }
    public decimal Tokens { get; init; }
    public string Reference { get; init; } = "";
    public string Submitted { get; init; } = "";
    public string Status { get; init; } = "Pending";
}

public record AdminTransactionDto
{
    public string Reference { get; init; } = "";
    public string Institution { get; init; } = "";
    public string Type { get; init; } = ""; // Wallet Top-Up, API Call
    public decimal Amount { get; init; }
    public decimal Tokens { get; init; }
    public string Date { get; init; } = "";
}

// ─── Admin Pricing ───
public record AdminPricingDto
{
    public string Type { get; init; } = ""; // NIN, BVN, Phone
    public decimal InstitutionCharge { get; init; }
    public decimal NimcCost { get; init; }
    public decimal TokenCost { get; init; }
    public decimal Margin => InstitutionCharge - NimcCost;
    public decimal MarginPct => InstitutionCharge > 0 ? Math.Round(Margin / InstitutionCharge * 100, 1) : 0;
}

public record UpdatePricingRequest
{
    public decimal InstitutionCharge { get; set; }
    public decimal NimcCost { get; set; }
}

// ─── Per-Institution Pricing ───
public record InstitutionPricingAdminDto
{
    public Guid InstitutionId { get; init; }
    public string InstitutionName { get; init; } = "";
    public string Email { get; init; } = "";
    public string Status { get; init; } = "";
    public decimal NinPrice { get; init; }
    public decimal BvnPrice { get; init; }
    public decimal PhonePrice { get; init; }
    public decimal NinCost { get; init; }
    public decimal BvnCost { get; init; }
    public decimal PhoneCost { get; init; }
    public decimal TotalSpendMtd { get; init; }
    public int TotalCallsMtd { get; init; }
}

public record UpdateInstitutionPricingRequest
{
    public decimal NinPrice { get; set; }
    public decimal BvnPrice { get; set; }
    public decimal PhonePrice { get; set; }
    public decimal NinCost { get; set; }
    public decimal BvnCost { get; set; }
    public decimal PhoneCost { get; set; }
}

// ─── API Audit ───
public class AdminApiKeyDto
{
    public Guid Id { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? OutletId { get; set; }
    public string Scope { get; set; } = "institution";
    public string InstitutionName { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Status { get; set; } = string.Empty;
    public long CallCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

// ─── Paginated List ───
public record PaginatedList<T>
{
    public List<T> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
}

// ─── Admin Management ───
public record AdminUserDto
{
    public Guid UserId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastLoginAt { get; init; }
}

public record InviteAdminRequest
{
    public string Email { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}

// ─── Audit Log ───
public record AuditLogEntryDto
{
    public Guid Id { get; init; }
    public string? ActorEmail { get; init; }
    public string ActorType { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Entity { get; init; } = string.Empty;
    public string? DetailsJson { get; init; }
    public Guid EntityId { get; init; }
    public DateTime CreatedAt { get; init; }
}

// ─── Low Balance Alert ───
public record LowBalanceAlertDto
{
    public string InstitutionId { get; init; } = string.Empty;
    public string InstitutionName { get; init; } = string.Empty;
    public string ContactEmail { get; init; } = string.Empty;
    public decimal CurrentBalance { get; init; }
    public decimal AlertThreshold { get; init; }
    public int CallsLast30Days { get; init; }
    public decimal AvgDailySpend { get; init; }
}

// ─── Role Management ───
public record UpdateRoleRequest
{
    public string Role { get; init; } = string.Empty;
}

public class CreditWalletRequest
{
    public decimal Amount { get; set; }
    public string? Description { get; set; }
}
