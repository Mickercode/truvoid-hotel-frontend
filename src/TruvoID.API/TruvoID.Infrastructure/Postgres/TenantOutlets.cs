namespace TruvoID.Infrastructure.Postgres;

/// <summary>
/// Outlet creation inside an Organization's schema. Only an Organization-scope
/// session can create Outlets — RLS rejects it from an Outlet scope. The wallet rule
/// (§2.3) is applied here and enforced again by a trigger in the schema.
/// </summary>
public static class TenantOutlets
{
    public static async Task<Guid> CreateAsync(TenantSession session, string name, Guid? createdByUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Outlet name is required.", nameof(name));

        string orgType;
        await using (var cmd = session.CreateCommand("SELECT org_type FROM tenant"))
            orgType = (string)(await cmd.ExecuteScalarAsync(ct))!;

        // Institution Outlets share the Organization wallet; Agency Outlets get their own.
        Guid walletId;
        await using (var cmd = session.CreateCommand(orgType == "agency"
            ? "INSERT INTO wallet (kind) VALUES ('outlet') RETURNING id"
            : "SELECT id FROM wallet WHERE kind = 'organization'"))
        {
            walletId = (Guid)(await cmd.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException("Only an Organization-scope session can create Outlets."));
        }

        await using (var cmd = session.CreateCommand("""
            INSERT INTO outlet (name, wallet_id, created_by_user_id)
            VALUES (@name, @walletId, @createdBy)
            RETURNING id
            """))
        {
            cmd.Parameters.AddWithValue("name", name.Trim());
            cmd.Parameters.AddWithValue("walletId", walletId);
            cmd.Parameters.AddWithValue("createdBy", (object?)createdByUserId ?? DBNull.Value);
            return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        }
    }
}
