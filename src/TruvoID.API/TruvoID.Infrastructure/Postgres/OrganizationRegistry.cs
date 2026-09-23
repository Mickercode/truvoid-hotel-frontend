using Npgsql;

namespace TruvoID.Infrastructure.Postgres;

public enum OrganizationType
{
    Institution,
    Agency
}

/// <summary>
/// Registers Organizations in the control plane. Runs as the runtime app role: it
/// only records the Organization as 'pending' — the schema and database role are
/// created afterwards by <see cref="TenantProvisioner"/>, which holds DDL rights.
/// </summary>
public sealed class OrganizationRegistry(NpgsqlDataSource controlPlane)
{
    public async Task<Guid> RegisterAsync(string name, OrganizationType type, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Organization name is required.", nameof(name));

        var id = Guid.NewGuid();
        var slug = $"org_{id:N}";

        await using var cmd = controlPlane.CreateCommand("""
            INSERT INTO control.organization (id, name, type, schema_name, db_role_name)
            VALUES (@id, @name, @type, @schema, @role)
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", name.Trim());
        cmd.Parameters.AddWithValue("type", ToDbValue(type));
        cmd.Parameters.AddWithValue("schema", slug);
        cmd.Parameters.AddWithValue("role", $"{slug}_rw");
        await cmd.ExecuteNonQueryAsync(ct);

        return id;
    }

    internal static string ToDbValue(OrganizationType type) => type switch
    {
        OrganizationType.Institution => "institution",
        OrganizationType.Agency => "agency",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}
