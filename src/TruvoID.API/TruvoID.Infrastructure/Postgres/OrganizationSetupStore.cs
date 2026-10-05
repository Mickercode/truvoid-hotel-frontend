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
    IReadOnlyList<OrganizationSetupDocument> Documents,
    string? ReviewNote = null,
    DateTime? SubmittedAt = null,
    DateTime? ReviewedAt = null);

/// <summary>Raised when a profile can't be edited because it's under review or approved.</summary>
public sealed class SetupLockedException(string status) : InvalidOperationException(status == "approved"
    ? "Your organization profile is approved. Contact TruvoID support to change it."
    : "Your organization profile is under review and can't be changed until the review is finished.");

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
        string? reviewNote = null;
        DateTime? submittedAt = null, reviewedAt = null;

        await using (var command = dataSource.CreateCommand("""
            SELECT general_info::text, contacts::text, business::text, ownership::text,
                   directors::text, services::text, integration::text, compliance::text,
                   legal::text, access_level, attested_at, status, review_note, submitted_at, reviewed_at
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
            reviewNote = reader.IsDBNull(12) ? null : reader.GetString(12);
            submittedAt = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTime>(13);
            reviewedAt = reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTime>(14);
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

        return new OrganizationSetupSnapshot(organizationId, sections, accessLevel, attestedAt, status, documents, reviewNote, submittedAt, reviewedAt);
    }

    public async Task SaveSectionAsync(Guid organizationId, string section, JsonElement payload, CancellationToken ct = default)
    {
        if (!SectionColumns.TryGetValue(section, out var column))
            throw new ArgumentException("Unknown organization setup section.", nameof(section));
        await EnsureEditableAsync(organizationId, ct);
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
        await EnsureEditableAsync(organizationId, ct);
        await using var command = dataSource.CreateCommand("UPDATE control.organization_setup SET access_level = @accessLevel, updated_at = now() WHERE organization_id = @organizationId");
        command.Parameters.AddWithValue("organizationId", organizationId);
        command.Parameters.AddWithValue("accessLevel", accessLevel);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Moves a draft (or a profile sent back for changes) to 'submitted'. False if it's already under review or approved.</summary>
    public async Task<bool> SubmitAsync(Guid organizationId, CancellationToken ct = default)
    {
        await EnsureAsync(organizationId, ct);
        await using var command = dataSource.CreateCommand("""
            UPDATE control.organization_setup
            SET status = 'submitted', submitted_at = now(), updated_at = now()
            WHERE organization_id = @organizationId AND status IN ('incomplete', 'needs_changes')
            """);
        command.Parameters.AddWithValue("organizationId", organizationId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Platform-admin decision on a submitted profile: approve (unlocks live verification)
    /// or send back with a note. False if the profile isn't currently submitted.
    /// </summary>
    public async Task<bool> ReviewAsync(Guid organizationId, bool approve, string? note, Guid reviewerId, CancellationToken ct = default)
    {
        if (!approve && string.IsNullOrWhiteSpace(note))
            throw new ArgumentException("Tell the organization what needs to change.");
        await EnsureAsync(organizationId, ct);
        // Approval only makes sense for a profile the organization actually submitted.
        // Sending a profile back with guidance is also allowed before submission, so a
        // reviewer can prompt an organization that hasn't finished.
        var eligible = approve
            ? "status = 'submitted'"
            : "status IN ('submitted', 'incomplete', 'needs_changes')";
        await using var command = dataSource.CreateCommand($"""
            UPDATE control.organization_setup
            SET status = @status, review_note = @note, reviewed_at = now(), reviewed_by = @reviewer, updated_at = now()
            WHERE organization_id = @organizationId AND {eligible}
            """);
        command.Parameters.AddWithValue("status", approve ? "approved" : "needs_changes");
        command.Parameters.AddWithValue("note", string.IsNullOrWhiteSpace(note) ? DBNull.Value : note.Trim());
        command.Parameters.AddWithValue("reviewer", reviewerId);
        command.Parameters.AddWithValue("organizationId", organizationId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Live verification is allowed for approved Institutions. Agencies are created by
    /// TruvoID Ops (build doc §2.4), so they're live from the start.
    /// </summary>
    public async Task<bool> IsLiveEnabledAsync(Guid organizationId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT o.type = 'agency' OR coalesce(s.status = 'approved', false)
            FROM control.organization o
            LEFT JOIN control.organization_setup s ON s.organization_id = o.id
            WHERE o.id = @organizationId
            """);
        command.Parameters.AddWithValue("organizationId", organizationId);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    public async Task<(string FileName, string ContentType, byte[] Content)?> GetDocumentAsync(Guid organizationId, Guid documentId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT file_name, content_type, content FROM control.organization_document WHERE id = @id AND organization_id = @organizationId");
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("organizationId", organizationId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetString(0), reader.GetString(1), reader.GetFieldValue<byte[]>(2))
            : null;
    }

    private async Task EnsureEditableAsync(Guid organizationId, CancellationToken ct)
    {
        await EnsureAsync(organizationId, ct);
        await using var command = dataSource.CreateCommand("SELECT status FROM control.organization_setup WHERE organization_id = @organizationId");
        command.Parameters.AddWithValue("organizationId", organizationId);
        if (await command.ExecuteScalarAsync(ct) is string status and ("submitted" or "approved"))
            throw new SetupLockedException(status);
    }

    public async Task SetAttestationAsync(Guid organizationId, bool accepted, CancellationToken ct = default)
    {
        await EnsureEditableAsync(organizationId, ct);
        await using var command = dataSource.CreateCommand("UPDATE control.organization_setup SET attested_at = CASE WHEN @accepted THEN now() ELSE NULL END, updated_at = now() WHERE organization_id = @organizationId");
        command.Parameters.AddWithValue("organizationId", organizationId);
        command.Parameters.AddWithValue("accepted", accepted);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<OrganizationSetupDocument> AddDocumentAsync(Guid organizationId, Guid userId, string type, string fileName, string contentType, byte[] content, CancellationToken ct = default)
    {
        if (content.Length == 0 || content.Length > 10 * 1024 * 1024)
            throw new ArgumentException("Documents must be between 1 byte and 10 MB.");
        await EnsureEditableAsync(organizationId, ct);
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
