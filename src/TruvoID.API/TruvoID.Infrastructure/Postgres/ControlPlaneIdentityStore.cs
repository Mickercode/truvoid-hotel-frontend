using Npgsql;

namespace TruvoID.Infrastructure.Postgres;

public sealed record ControlPlaneUser(
    Guid Id,
    Guid? OrganizationId,
    Guid? OutletId,
    string Email,
    string? FullName,
    string CredentialHash,
    string Role,
    string Status,
    string OrganizationName,
    // control.organization.status ('pending' until the worker provisions it); null for platform admins
    string? OrganizationStatus = null,
    DateTime? LockedUntil = null,
    int FailedLoginAttempts = 0);

public sealed record RegisteredIdentity(Guid UserId, Guid OrganizationId);
public sealed record AgencyInvitation(Guid InvitationId, Guid OrganizationId, Guid UserId);
public sealed record TeamMember(Guid Id, string Email, string? FullName, string Role, string Status, Guid? OutletId, DateTime CreatedAt, DateTime? LastLoginAt);
public sealed record UserInvitation(Guid InvitationId, Guid OrganizationId, Guid UserId);

/// <summary>
/// Central identity access. Authentication must resolve the Organization before
/// the API can open a dedicated tenant connection.
/// </summary>
public sealed class ControlPlaneIdentityStore(NpgsqlDataSource controlPlane)
{
    public Task<RegisteredIdentity> RegisterInstitutionAsync(
        string organizationName,
        string contactEmail,
        string adminName,
        string adminEmail,
        string password,
        CancellationToken ct = default) =>
        RegisterOrganizationAsync(organizationName, OrganizationType.Institution, adminName, adminEmail, password, ct);

    public async Task<AgencyInvitation> InviteAgencyAsync(
        string organizationName,
        string adminName,
        string adminEmail,
        Guid invitedByUserId,
        string tokenHash,
        DateTime expiresAt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(organizationName) || string.IsNullOrWhiteSpace(adminEmail))
            throw new ArgumentException("Agency name and administrator email are required.");

        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        await using var conn = await controlPlane.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var organization = new NpgsqlCommand("""
            INSERT INTO control.organization (id, name, type, schema_name, db_role_name)
            VALUES (@id, @name, 'agency', @schema, @role)
            """, conn, tx))
        {
            organization.Parameters.AddWithValue("id", organizationId);
            organization.Parameters.AddWithValue("name", organizationName.Trim());
            organization.Parameters.AddWithValue("schema", $"org_{organizationId:N}");
            organization.Parameters.AddWithValue("role", $"org_{organizationId:N}_rw");
            await organization.ExecuteNonQueryAsync(ct);
        }

        await using (var user = new NpgsqlCommand("""
            INSERT INTO control.app_user
                (id, email, full_name, credential_hash, organization_id, role, status)
            VALUES (@id, @email, @fullName, @credentialHash, @organizationId, 'agency_admin', 'invited')
            """, conn, tx))
        {
            user.Parameters.AddWithValue("id", userId);
            user.Parameters.AddWithValue("email", adminEmail.Trim().ToLowerInvariant());
            user.Parameters.AddWithValue("fullName", (object?)adminName?.Trim() ?? DBNull.Value);
            user.Parameters.AddWithValue("credentialHash", PasswordHasher.Hash(Guid.NewGuid().ToString("N")));
            user.Parameters.AddWithValue("organizationId", organizationId);
            await user.ExecuteNonQueryAsync(ct);
        }

        await using (var invitation = new NpgsqlCommand("""
            INSERT INTO control.agency_invitation
                (id, organization_id, user_id, token_hash, expires_at, created_by_user_id)
            VALUES (@id, @organizationId, @userId, @tokenHash, @expiresAt, @createdBy)
            """, conn, tx))
        {
            invitation.Parameters.AddWithValue("id", invitationId);
            invitation.Parameters.AddWithValue("organizationId", organizationId);
            invitation.Parameters.AddWithValue("userId", userId);
            invitation.Parameters.AddWithValue("tokenHash", tokenHash);
            invitation.Parameters.AddWithValue("expiresAt", expiresAt);
            invitation.Parameters.AddWithValue("createdBy", invitedByUserId);
            await invitation.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new AgencyInvitation(invitationId, organizationId, userId);
    }

    public async Task<ControlPlaneUser?> AcceptAgencyInvitationAsync(
        string tokenHash,
        string password,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new ArgumentException("Password must be at least 8 characters.");

        await using var conn = await controlPlane.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid userId;
        await using (var find = new NpgsqlCommand("""
            SELECT user_id
            FROM control.agency_invitation
            WHERE token_hash = @tokenHash
              AND accepted_at IS NULL
              AND expires_at > now()
            FOR UPDATE
            """, conn, tx))
        {
            find.Parameters.AddWithValue("tokenHash", tokenHash);
            var value = await find.ExecuteScalarAsync(ct);
            if (value is not Guid foundUserId)
                return null;
            userId = foundUserId;
        }

        await using (var user = new NpgsqlCommand("""
            UPDATE control.app_user
            SET credential_hash = @credentialHash, status = 'active', updated_at = now()
            WHERE id = @id AND role = 'agency_admin'
            """, conn, tx))
        {
            user.Parameters.AddWithValue("credentialHash", PasswordHasher.Hash(password));
            user.Parameters.AddWithValue("id", userId);
            if (await user.ExecuteNonQueryAsync(ct) != 1)
                return null;
        }

        await using (var accepted = new NpgsqlCommand(
            "UPDATE control.agency_invitation SET accepted_at = now() WHERE token_hash = @tokenHash", conn, tx))
        {
            accepted.Parameters.AddWithValue("tokenHash", tokenHash);
            await accepted.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return await FindByIdAsync(userId, ct);
    }

    public async Task<IReadOnlyList<TeamMember>> ListTeamAsync(Guid organizationId, CancellationToken ct = default)
    {
        await using var command = controlPlane.CreateCommand("""
            SELECT id, email, full_name, role, status, outlet_id, created_at, last_login_at
            FROM control.app_user
            WHERE organization_id = @organizationId
            ORDER BY created_at
            """);
        command.Parameters.AddWithValue("organizationId", organizationId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var members = new List<TeamMember>();
        while (await reader.ReadAsync(ct))
            members.Add(new TeamMember(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.GetFieldValue<DateTime>(6), reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTime>(7)));
        return members;
    }

    public async Task<UserInvitation> InviteUserAsync(
        Guid organizationId,
        string email,
        string fullName,
        string role,
        Guid? outletId,
        Guid createdByUserId,
        string tokenHash,
        DateTime expiresAt,
        CancellationToken ct = default)
    {
        var userId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        await using var conn = await controlPlane.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var user = new NpgsqlCommand("""
            INSERT INTO control.app_user (id, email, full_name, credential_hash, organization_id, outlet_id, role, status)
            VALUES (@id, @email, @fullName, @credentialHash, @organizationId, @outletId, @role, 'invited')
            """, conn, tx))
        {
            user.Parameters.AddWithValue("id", userId);
            user.Parameters.AddWithValue("email", email.Trim().ToLowerInvariant());
            user.Parameters.AddWithValue("fullName", fullName.Trim());
            user.Parameters.AddWithValue("credentialHash", PasswordHasher.Hash(Guid.NewGuid().ToString("N")));
            user.Parameters.AddWithValue("organizationId", organizationId);
            user.Parameters.AddWithValue("outletId", (object?)outletId ?? DBNull.Value);
            user.Parameters.AddWithValue("role", role);
            await user.ExecuteNonQueryAsync(ct);
        }
        await using (var invitation = new NpgsqlCommand("""
            INSERT INTO control.user_invitation (id, organization_id, user_id, token_hash, expires_at, created_by_user_id)
            VALUES (@id, @organizationId, @userId, @tokenHash, @expiresAt, @createdBy)
            """, conn, tx))
        {
            invitation.Parameters.AddWithValue("id", invitationId);
            invitation.Parameters.AddWithValue("organizationId", organizationId);
            invitation.Parameters.AddWithValue("userId", userId);
            invitation.Parameters.AddWithValue("tokenHash", tokenHash);
            invitation.Parameters.AddWithValue("expiresAt", expiresAt);
            invitation.Parameters.AddWithValue("createdBy", createdByUserId);
            await invitation.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new UserInvitation(invitationId, organizationId, userId);
    }

    public async Task<ControlPlaneUser?> AcceptUserInvitationAsync(string tokenHash, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new ArgumentException("Password must be at least 8 characters.");
        await using var conn = await controlPlane.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid userId;
        await using (var find = new NpgsqlCommand("SELECT user_id FROM control.user_invitation WHERE token_hash = @tokenHash AND accepted_at IS NULL AND expires_at > now() FOR UPDATE", conn, tx))
        {
            find.Parameters.AddWithValue("tokenHash", tokenHash);
            var value = await find.ExecuteScalarAsync(ct);
            if (value is not Guid found) return null;
            userId = found;
        }
        await using (var user = new NpgsqlCommand("UPDATE control.app_user SET credential_hash = @credentialHash, status = 'active', updated_at = now() WHERE id = @id", conn, tx))
        {
            user.Parameters.AddWithValue("credentialHash", PasswordHasher.Hash(password));
            user.Parameters.AddWithValue("id", userId);
            if (await user.ExecuteNonQueryAsync(ct) != 1) return null;
        }
        await using (var accepted = new NpgsqlCommand("UPDATE control.user_invitation SET accepted_at = now() WHERE token_hash = @tokenHash", conn, tx))
        {
            accepted.Parameters.AddWithValue("tokenHash", tokenHash);
            await accepted.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return await FindByIdAsync(userId, ct);
    }

    public async Task<bool> SetUserStatusAsync(Guid organizationId, Guid userId, string status, CancellationToken ct = default)
    {
        // Administrators are managed by TruvoID platform staff, not by each other; this
        // stops an admin disabling/demoting a peer (or the org owner) and locking the org out.
        await using var command = controlPlane.CreateCommand("""
            UPDATE control.app_user SET status = @status, updated_at = now()
            WHERE id = @userId AND organization_id = @organizationId
              AND role NOT IN ('institution_admin', 'agency_admin', 'platform_admin')
            """);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("organizationId", organizationId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> SetUserRoleAsync(Guid organizationId, Guid userId, string role, CancellationToken ct = default)
    {
        await using var command = controlPlane.CreateCommand("""
            UPDATE control.app_user SET role = @role, updated_at = now()
            WHERE id = @userId AND organization_id = @organizationId
              AND role NOT IN ('institution_admin', 'agency_admin', 'platform_admin')
            """);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("organizationId", organizationId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<RegisteredIdentity> RegisterOrganizationAsync(
        string organizationName,
        OrganizationType type,
        string adminName,
        string adminEmail,
        string password,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(organizationName) || string.IsNullOrWhiteSpace(adminEmail))
            throw new ArgumentException("Organization name and administrator email are required.");

        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var adminRole = type == OrganizationType.Agency ? "agency_admin" : "institution_admin";
        await using var conn = await controlPlane.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var organization = new NpgsqlCommand("""
            INSERT INTO control.organization (id, name, type, schema_name, db_role_name)
            VALUES (@id, @name, @type, @schema, @role)
            """, conn, tx))
        {
            organization.Parameters.AddWithValue("id", organizationId);
            organization.Parameters.AddWithValue("name", organizationName.Trim());
            organization.Parameters.AddWithValue("type", OrganizationRegistry.ToDbValue(type));
            organization.Parameters.AddWithValue("schema", $"org_{organizationId:N}");
            organization.Parameters.AddWithValue("role", $"org_{organizationId:N}_rw");
            await organization.ExecuteNonQueryAsync(ct);
        }

        await using (var user = new NpgsqlCommand("""
            INSERT INTO control.app_user
                (id, email, full_name, credential_hash, organization_id, role, status)
            VALUES (@id, @email, @fullName, @credentialHash, @organizationId, @role, 'active')
            """, conn, tx))
        {
            user.Parameters.AddWithValue("id", userId);
            user.Parameters.AddWithValue("email", adminEmail.Trim().ToLowerInvariant());
            user.Parameters.AddWithValue("fullName", (object?)adminName?.Trim() ?? DBNull.Value);
            user.Parameters.AddWithValue("credentialHash", PasswordHasher.Hash(password));
            user.Parameters.AddWithValue("organizationId", organizationId);
            user.Parameters.AddWithValue("role", adminRole);
            await user.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new RegisteredIdentity(userId, organizationId);
    }

    public async Task<ControlPlaneUser?> FindByEmailAsync(string email, CancellationToken ct = default) =>
        await FindAsync("WHERE lower(u.email) = lower(@email)", command => command.Parameters.AddWithValue("email", email.Trim()), ct);

    public async Task<ControlPlaneUser?> FindByIdAsync(Guid userId, CancellationToken ct = default) =>
        await FindAsync("WHERE u.id = @id", command => command.Parameters.AddWithValue("id", userId), ct);

    public async Task<bool> UpdatePasswordAsync(Guid userId, string passwordHash, CancellationToken ct = default)
    {
        await using var command = controlPlane.CreateCommand("""
            UPDATE control.app_user
            SET credential_hash = @credentialHash, updated_at = now()
            WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("credentialHash", passwordHash);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Consecutive failed sign-ins before an account is locked.</summary>
    public const int MaxFailedLoginAttempts = 5;

    /// <summary>How long an account stays locked after too many failed sign-ins.</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public async Task MarkLoginAsync(Guid userId, CancellationToken ct = default)
    {
        await using var command = controlPlane.CreateCommand(
            "UPDATE control.app_user SET last_login_at = now() WHERE id = @id");
        command.Parameters.AddWithValue("id", userId);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Counts a failed sign-in and locks the account once the threshold is reached.</summary>
    public async Task RecordLoginFailureAsync(Guid userId, CancellationToken ct = default)
    {
        await using var command = controlPlane.CreateCommand("""
            UPDATE control.app_user
            SET failed_login_attempts = failed_login_attempts + 1,
                locked_until = CASE
                    WHEN failed_login_attempts + 1 >= @max THEN now() + @lockout
                    ELSE locked_until END,
                updated_at = now()
            WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("max", MaxFailedLoginAttempts);
        command.Parameters.AddWithValue("lockout", LockoutDuration);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Clears the failure counter and records a successful sign-in.</summary>
    public async Task RecordLoginSuccessAsync(Guid userId, CancellationToken ct = default)
    {
        await using var command = controlPlane.CreateCommand("""
            UPDATE control.app_user
            SET failed_login_attempts = 0, locked_until = NULL, last_login_at = now(), updated_at = now()
            WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", userId);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Suspends the organization and disables its members; returns their ids (to end sessions), or null if not found.</summary>
    public async Task<IReadOnlyList<Guid>?> DeactivateOrganizationAsync(Guid organizationId, CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var organization = new NpgsqlCommand("UPDATE control.organization SET status = 'suspended', updated_at = now() WHERE id = @id", conn, tx))
        {
            organization.Parameters.AddWithValue("id", organizationId);
            if (await organization.ExecuteNonQueryAsync(ct) != 1)
                return null;
        }

        // app_user.status allows invited | active | disabled — 'suspended' would violate the check.
        var members = new List<Guid>();
        await using (var users = new NpgsqlCommand(
            "UPDATE control.app_user SET status = 'disabled', updated_at = now() WHERE organization_id = @id RETURNING id", conn, tx))
        {
            users.Parameters.AddWithValue("id", organizationId);
            await using var reader = await users.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                members.Add(reader.GetGuid(0));
        }

        await tx.CommitAsync(ct);
        return members;
    }

    private async Task<ControlPlaneUser?> FindAsync(
        string predicate,
        Action<NpgsqlCommand> bind,
        CancellationToken ct)
    {
        await using var command = controlPlane.CreateCommand($"""
            SELECT u.id, u.organization_id, u.outlet_id, u.email, u.full_name,
                   u.credential_hash, u.role, u.status, coalesce(o.name, ''), o.status,
                   u.locked_until, u.failed_login_attempts
            FROM control.app_user u
            LEFT JOIN control.organization o ON o.id = u.organization_id
            {predicate}
            LIMIT 1
            """);
        bind(command);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new ControlPlaneUser(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTime>(10),
            reader.GetInt32(11));
    }
}
