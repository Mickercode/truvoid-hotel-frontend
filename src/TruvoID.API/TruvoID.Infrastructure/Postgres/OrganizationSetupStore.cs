using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace TruvoID.Infrastructure.Postgres;

public sealed record OrganizationSetupDocument(
    Guid Id,
    string DocumentType,
    string FileName,
    string ContentType,
    string Status,
    DateTime UploadedAt);

public sealed record OrganizationSetupSnapshot(
    Guid OrganizationId,
    IReadOnlyDictionary<string, string> Sections,
    short? AccessLevel,
    DateTime? AttestedAt,
    string Status,
    IReadOnlyList<OrganizationSetupDocument> Documents);

public sealed class OrganizationSetupStore(NpgsqlDataSource dataSource)
{
    private static readonly IReadOnlyDictionary<string, string> SectionColumns =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["general"] = "general_info",
            ["contacts"] = "contacts",
            ["business"] = "business",
            ["ownership"] = "ownership",
            ["directors"] = "directors",
            ["services"] = "services",
            ["integration"] = "integration",
            ["compliance"] = "compliance",
            ["legal"] = "legal"
        };

    public async Task<OrganizationSetupSnapshot> GetAsync(Guid organizationId, CancellationToken ct = default)
    {
        await EnsureAsync(organizationId, ct);
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        short? accessLevel = null;
        DateTime? attestedAt = null;
        string status;

        await using (var command = dataSource.CreateCommand("""
            SELECT general_info::text, contacts::text, business::text, ownership::text,
                   directors::text, services::text, integration::text, compliance::text,
                   legal::text, access_level, attested_at, status
            FROM control.organization_setup
            WHERE organization_id = @organizationId
            """))
        {
            command.Parameters.AddWithValue("organizationId", organizationId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var names = SectionColumns.Keys.ToArray();
            for (var index = 0; index < names.Length; index++)
                sections[names[index]] = reader.GetString(index);
            accessLevel = reader.IsDBNull(9) ? null : reader.GetFieldValue<short>(9);
            attestedAt = reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTime>(10);
            status = reader.GetString(11);
        }

        var documents = new List<OrganizationSetupDocument>();
        await using (var command = dataSource.CreateCommand("""
            SELECT id, document_type, file_name, content_type, status, uploaded_at
            FROM control.organization_document
            WHERE organization_id = @organizationId
            ORDER BY uploaded_at DESC
            """))
        {
            command.Parameters.AddWithValue("organizationId", organizationId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                documents.Add(new OrganizationSetupDocument(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTime>(5)));
        }

        return new OrganizationSetupSnapshot(organizationId, sections, accessLevel, attestedAt, status, documents);
    }

    public async Task SaveSectionAsync(Guid organizationId, string section, JsonElement payload, CancellationToken ct = default)
    {
        if (!SectionColumns.TryGetValue(section, out var column))
            throw new ArgumentException("Unknown organization setup section.", nameof(section));
        await EnsureAsync(organizationId, ct);
        await using var command = dataSource.CreateCommand($"""
            UPDATE control.organization_setup
            SET {column} = @payload::jsonb, status = CASE WHEN status = 'needs_changes' THEN 'incomplete' ELSE status END, updated_at = now()
            WHERE organization_id = @organizationId
            """);
        command.Parameters.AddWithValue("organizationId", organizationId);
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload.GetRawText() });
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveAccessLevelAsync(Guid organizationId, short accessLevel, CancellationToken ct = default)
    {
        if (accessLevel is < 1 or > 5) throw new ArgumentException("Access level must be between 1 and 5.");
        await EnsureAsync(organizationId, ct);
        await using var command = dataSource.CreateCommand("UPDATE control.organization_setup SET access_level = @accessLevel, updated_at = now() WHERE organization_id = @organizationId");
        command.Parameters.AddWithValue("organizationId", organizationId);
        command.Parameters.AddWithValue("accessLevel", accessLevel);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SubmitAsync(Guid organizationId, CancellationToken ct = default)
    {
        await EnsureAsync(organizationId, ct);
        await using var command = dataSource.CreateCommand("UPDATE control.organization_setup SET status = 'submitted', updated_at = now() WHERE organization_id = @organizationId");
        command.Parameters.AddWithValue("organizationId", organizationId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SetAttestationAsync(Guid organizationId, bool accepted, CancellationToken ct = default)
    {
        await EnsureAsync(organizationId, ct);
        await using var command = dataSource.CreateCommand("UPDATE control.organization_setup SET attested_at = CASE WHEN @accepted THEN now() ELSE NULL END, updated_at = now() WHERE organization_id = @organizationId");
        command.Parameters.AddWithValue("organizationId", organizationId);
        command.Parameters.AddWithValue("accepted", accepted);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<OrganizationSetupDocument> AddDocumentAsync(Guid organizationId, Guid userId, string type, string fileName, string contentType, byte[] content, CancellationToken ct = default)
    {
        if (content.Length == 0 || content.Length > 10 * 1024 * 1024)
            throw new ArgumentException("Documents must be between 1 byte and 10 MB.");
        await EnsureAsync(organizationId, ct);
        await using var command = dataSource.CreateCommand("""
            INSERT INTO control.organization_document
                (organization_id, document_type, file_name, content_type, content, uploaded_by)
            VALUES (@organizationId, @type, @fileName, @contentType, @content, @userId)
            RETURNING id, document_type, file_name, content_type, status, uploaded_at
            """);
        command.Parameters.AddWithValue("organizationId", organizationId);
        command.Parameters.AddWithValue("type", type.Trim());
        command.Parameters.AddWithValue("fileName", fileName.Trim());
        command.Parameters.AddWithValue("contentType", contentType);
        command.Parameters.AddWithValue("content", content);
        command.Parameters.AddWithValue("userId", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new OrganizationSetupDocument(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTime>(5));
    }

    private async Task EnsureAsync(Guid organizationId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("INSERT INTO control.organization_setup (organization_id) VALUES (@organizationId) ON CONFLICT (organization_id) DO NOTHING");
        command.Parameters.AddWithValue("organizationId", organizationId);
        await command.ExecuteNonQueryAsync(ct);
    }
}
