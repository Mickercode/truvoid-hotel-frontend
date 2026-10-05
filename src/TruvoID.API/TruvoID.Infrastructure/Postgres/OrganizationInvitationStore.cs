using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace TruvoID.Infrastructure.Postgres;

public sealed record EmailAvailability(bool Available, string? Reason);

public sealed record OrganizationInvitationRow(
    Guid Id,
    string OrganizationName,
    string OrganizationType,
    string AdminFullName,
    string AdminEmail,
    /// <summary>pending | expired | accepted | cancelled</summary>
    string Status,
    DateTime CreatedAt,
    DateTime LastSentAt,
    int SendCount,
    DateTime ExpiresAt,
    DateTime? AcceptedAt,
    Guid? AcceptedOrganizationId);

/// <summary>A freshly issued link: the raw token exists only here, never in the database.</summary>
public sealed record IssuedInvitation(OrganizationInvitationRow Invitation, string Token);

public sealed class InvitationException(string message, string code) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// TruvoID Ops invitations for new Institutions and Agencies. Nothing but the invitation
/// exists until it's accepted; acceptance creates the organization (pending, so the worker
/// provisions it) and its active administrator in one transaction.
/// </summary>
public sealed class OrganizationInvitationStore(NpgsqlDataSource controlPlane)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private const string RowColumns = """
        id, organization_name, organization_type, admin_full_name, admin_email,
        CASE WHEN accepted_at IS NOT NULL THEN 'accepted'
             WHEN cancelled_at IS NOT NULL THEN 'cancelled'
             WHEN expires_at <= now() THEN 'expired'
             ELSE 'pending' END,
        created_at, last_sent_at, send_count, expires_at, accepted_at, accepted_organization_id
        """;

    /// <summary>Can this email become a new organization's administrator?</summary>
    public async Task<EmailAvailability> CheckEmailAsync(string email, CancellationToken ct = default)
    {
        await using var command = controlPlane.CreateCommand("""
            SELECT u.role, coalesce(o.name, '')
            FROM control.app_user u LEFT JOIN control.organization o ON o.id = u.organization_id
            WHERE lower(u.email) = lower(@email)
            """);
        command.Parameters.AddWithValue("email", email.Trim());
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
                return new EmailAvailability(false, reader.GetString(0) == "platform_admin"
                    ? "This email is a TruvoID platform administrator."
                    : $"This email already signs in to {reader.GetString(1)}.");
        }

        // Expired invitations no longer block a new one; the partial unique index is
        // only cleared by cancelling them, which CreateAsync does before inserting.
        await using var open = controlPlane.CreateCommand("""
            SELECT organization_name FROM control.organization_invitation
            WHERE lower(admin_email) = lower(@email)
              AND accepted_at IS NULL AND cancelled_at IS NULL AND expires_at > now()
            """);
        open.Parameters.AddWithValue("email", email.Trim());
        return await open.ExecuteScalarAsync(ct) is string pendingFor
            ? new EmailAvailability(false, $"There's already an open invitation for this email ({pendingFor}). Resend or cancel it instead.")
            : new EmailAvailability(true, null);
    }

    public async Task<IssuedInvitation> CreateAsync(
        string organizationName, string organizationType, string adminFullName, string adminEmail,
        Guid createdByUserId, CancellationToken ct = default)
    {
        if (await CheckEmailAsync(adminEmail, ct) is { Available: false } taken)
            throw new InvitationException(taken.Reason!, "email_taken");

        // Release the "one open invitation per email" unique index from any invitation
        // that has already expired, otherwise a stale link blocks this email forever.
        await using (var expire = controlPlane.CreateCommand("""
            UPDATE control.organization_invitation SET cancelled_at = now()
            WHERE lower(admin_email) = lower(@email)
              AND accepted_at IS NULL AND cancelled_at IS NULL AND expires_at <= now()
            """))
        {
            expire.Parameters.AddWithValue("email", adminEmail.Trim());
            await expire.ExecuteNonQueryAsync(ct);
        }

        var token = NewToken();
        await using var command = controlPlane.CreateCommand($"""
            INSERT INTO control.organization_invitation
                (organization_name, organization_type, admin_full_name, admin_email, token_hash, expires_at, created_by_user_id)
            VALUES (@name, @type, @adminName, @email, @hash, @expires, @createdBy)
            RETURNING {RowColumns}
            """);
        command.Parameters.AddWithValue("name", organizationName.Trim());
        command.Parameters.AddWithValue("type", organizationType);
        command.Parameters.AddWithValue("adminName", adminFullName.Trim());
        command.Parameters.AddWithValue("email", adminEmail.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("hash", Hash(token));
        command.Parameters.AddWithValue("expires", DateTime.UtcNow.Add(Lifetime));
        command.Parameters.AddWithValue("createdBy", createdByUserId);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return new IssuedInvitation(Read(reader), token);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Lost a race with a concurrent invite for the same email.
            throw new InvitationException("There's already an open invitation for this email.", "email_taken");
        }
    }

    public async Task<IReadOnlyList<OrganizationInvitationRow>> ListAsync(CancellationToken ct = default)
    {
        await using var command = controlPlane.CreateCommand($"""
            SELECT {RowColumns} FROM control.organization_invitation ORDER BY created_at DESC LIMIT 500
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<OrganizationInvitationRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(Read(reader));
        return rows;
    }

    /// <summary>New link and a fresh 7 days; the previous link stops working. Works on expired invitations too.</summary>
    public async Task<IssuedInvitation> ResendAsync(Guid id, CancellationToken ct = default)
    {
        var token = NewToken();
        await using var command = controlPlane.CreateCommand($"""
            UPDATE control.organization_invitation
            SET token_hash = @hash, expires_at = @expires, last_sent_at = now(), send_count = send_count + 1
            WHERE id = @id AND accepted_at IS NULL AND cancelled_at IS NULL
            RETURNING {RowColumns}
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("hash", Hash(token));
        command.Parameters.AddWithValue("expires", DateTime.UtcNow.Add(Lifetime));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvitationException("Only open invitations can be resent.", "invitation_closed");
        return new IssuedInvitation(Read(reader), token);
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken ct = default)
    {
        await using var command = controlPlane.CreateCommand("""
            UPDATE control.organization_invitation SET cancelled_at = now()
            WHERE id = @id AND accepted_at IS NULL AND cancelled_at IS NULL
            """);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>What the accept page shows before the person sets a password.</summary>
    public async Task<OrganizationInvitationRow?> PreviewAsync(string token, CancellationToken ct = default)
    {
        await using var command = controlPlane.CreateCommand($"""
            SELECT {RowColumns} FROM control.organization_invitation WHERE token_hash = @hash
            """);
        command.Parameters.AddWithValue("hash", Hash(token ?? ""));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>
    /// Creates the organization (pending — the worker provisions it within seconds) and its
    /// active administrator, and closes the invitation, all in one transaction.
    /// </summary>
    public async Task<(Guid OrganizationId, Guid UserId, string Email)> AcceptAsync(string token, string passwordHash, CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        Guid invitationId;
        string name, type, adminName, email;
        await using (var find = new NpgsqlCommand("""
            SELECT id, organization_name, organization_type, admin_full_name, admin_email
            FROM control.organization_invitation
            WHERE token_hash = @hash AND accepted_at IS NULL AND cancelled_at IS NULL AND expires_at > now()
            FOR UPDATE
            """, conn, tx))
        {
            find.Parameters.AddWithValue("hash", Hash(token ?? ""));
            await using var reader = await find.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvitationException("This invitation link is invalid, expired, cancelled or already used. Ask TruvoID to resend it.", "invalid_invitation");
            (invitationId, name, type, adminName, email) =
                (reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
        }

        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using (var organization = new NpgsqlCommand("""
            INSERT INTO control.organization (id, name, type, schema_name, db_role_name)
            VALUES (@id, @name, @type, @schema, @role)
            """, conn, tx))
        {
            organization.Parameters.AddWithValue("id", organizationId);
            organization.Parameters.AddWithValue("name", name);
            organization.Parameters.AddWithValue("type", type);
            organization.Parameters.AddWithValue("schema", $"org_{organizationId:N}");
            organization.Parameters.AddWithValue("role", $"org_{organizationId:N}_rw");
            await organization.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await using var user = new NpgsqlCommand("""
                INSERT INTO control.app_user (id, email, full_name, credential_hash, organization_id, role, status)
                VALUES (@id, @email, @name, @hash, @org, @role, 'active')
                """, conn, tx);
            user.Parameters.AddWithValue("id", userId);
            user.Parameters.AddWithValue("email", email);
            user.Parameters.AddWithValue("name", adminName);
            user.Parameters.AddWithValue("hash", passwordHash);
            user.Parameters.AddWithValue("org", organizationId);
            user.Parameters.AddWithValue("role", type == "agency" ? "agency_admin" : "institution_admin");
            await user.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // The email signed up elsewhere after the invitation was sent; the transaction rolls back.
            throw new InvitationException("This email already has a TruvoID account. Ask TruvoID to invite a different email.", "email_taken");
        }

        await using (var close = new NpgsqlCommand("""
            UPDATE control.organization_invitation SET accepted_at = now(), accepted_organization_id = @org WHERE id = @id
            """, conn, tx))
        {
            close.Parameters.AddWithValue("org", organizationId);
            close.Parameters.AddWithValue("id", invitationId);
            await close.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return (organizationId, userId, email);
    }

    private static OrganizationInvitationRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.GetFieldValue<DateTime>(6), r.GetFieldValue<DateTime>(7), r.GetInt32(8), r.GetFieldValue<DateTime>(9),
        r.IsDBNull(10) ? null : r.GetFieldValue<DateTime>(10), r.IsDBNull(11) ? null : r.GetGuid(11));

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
