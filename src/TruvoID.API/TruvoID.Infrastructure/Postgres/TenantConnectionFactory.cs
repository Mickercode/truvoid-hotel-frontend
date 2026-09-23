using System.Collections.Concurrent;
using Npgsql;

namespace TruvoID.Infrastructure.Postgres;

/// <summary>Who a tenant transaction acts as: the whole Organization, or one of its Outlets.</summary>
public sealed record TenantScope
{
    private TenantScope(Guid organizationId, Guid? outletId)
    {
        OrganizationId = organizationId;
        OutletId = outletId;
    }

    public Guid OrganizationId { get; }
    public Guid? OutletId { get; }

    /// <summary>Institution/Agency admins and staff: sees every Outlet in the Organization.</summary>
    public static TenantScope Organization(Guid organizationId) => new(organizationId, null);

    /// <summary>Outlet owners/staff and Outlet API keys: sees only that Outlet's rows.</summary>
    public static TenantScope Outlet(Guid organizationId, Guid outletId) => new(organizationId, outletId);
}

/// <summary>
/// One transaction on an Organization's schema with its scope applied. Always
/// dispose it; anything not explicitly committed is rolled back.
/// </summary>
public sealed class TenantSession(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantScope scope)
    : IAsyncDisposable
{
    public NpgsqlConnection Connection { get; } = connection;
    public NpgsqlTransaction Transaction { get; } = transaction;
    public TenantScope Scope { get; } = scope;

    public NpgsqlCommand CreateCommand(string sql) => new(sql, Connection, Transaction);

    public Task CommitAsync(CancellationToken ct = default) => Transaction.CommitAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}

/// <summary>
/// Opens connections to an Organization's schema as that Organization's own database
/// role (build doc §3.1) — so even a routing bug can't read another Organization's
/// data: the database refuses it. One small pool per Organization, created on first
/// use; the password is decrypted from control.organization_db_credential.
/// </summary>
public sealed class TenantConnectionFactory(
    NpgsqlDataSource controlPlane,
    string baseConnectionString,
    TenantCredentialProtector protector) : IAsyncDisposable
{
    // Small per-Organization pools, released when idle, so hundreds of mostly-quiet
    // Organizations don't exhaust Postgres's connection limit.
    private const int MaxPoolSizePerOrganization = 5;
    private const int IdleLifetimeSeconds = 60;

    private readonly ConcurrentDictionary<Guid, Lazy<Task<NpgsqlDataSource>>> _dataSources = new();

    /// <summary>
    /// Begins a transaction on the Organization's schema with its scope set via
    /// SET LOCAL semantics, so the scope ends with the transaction and can never
    /// leak to the next request that reuses the pooled connection.
    /// </summary>
    public async Task<TenantSession> BeginAsync(TenantScope scope, CancellationToken ct = default)
    {
        var dataSource = await GetDataSourceAsync(scope.OrganizationId, ct);
        var conn = await dataSource.OpenConnectionAsync(ct);
        try
        {
            var tx = await conn.BeginTransactionAsync(ct);
            await using (var cmd = new NpgsqlCommand(
                "SELECT set_config('app.scope', @scope, true), set_config('app.outlet_id', @outlet, true)", conn, tx))
            {
                cmd.Parameters.AddWithValue("scope", scope.OutletId is null ? "org" : "outlet");
                cmd.Parameters.AddWithValue("outlet", scope.OutletId?.ToString() ?? string.Empty);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            return new TenantSession(conn, tx, scope);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    internal async Task<NpgsqlDataSource> GetDataSourceAsync(Guid organizationId, CancellationToken ct)
    {
        var lazy = _dataSources.GetOrAdd(organizationId,
            id => new Lazy<Task<NpgsqlDataSource>>(() => CreateDataSourceAsync(id, CancellationToken.None)));
        try
        {
            return await lazy.Value.WaitAsync(ct);
        }
        catch
        {
            // Don't cache failures (e.g. an Organization that isn't provisioned yet).
            _dataSources.TryRemove(new KeyValuePair<Guid, Lazy<Task<NpgsqlDataSource>>>(organizationId, lazy));
            throw;
        }
    }

    private async Task<NpgsqlDataSource> CreateDataSourceAsync(Guid organizationId, CancellationToken ct)
    {
        await using var cmd = controlPlane.CreateCommand("""
            SELECT o.schema_name, o.db_role_name, c.password_ciphertext, c.key_id
            FROM control.organization o
            JOIN control.organization_db_credential c ON c.organization_id = o.id
            WHERE o.id = @id AND o.status = 'active'
            """);
        cmd.Parameters.AddWithValue("id", organizationId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException($"Organization {organizationId} is not an active, provisioned tenant.");

        var schema = reader.GetString(0);
        var role = reader.GetString(1);
        var password = protector.Unprotect(organizationId, reader.GetString(3), reader.GetFieldValue<byte[]>(2));

        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Username = role,
            Password = password,
            SearchPath = schema,
            MaxPoolSize = MaxPoolSizePerOrganization,
            ConnectionIdleLifetime = IdleLifetimeSeconds,
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _dataSources.Values)
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
                await lazy.Value.Result.DisposeAsync();
        }
        _dataSources.Clear();
    }
}
