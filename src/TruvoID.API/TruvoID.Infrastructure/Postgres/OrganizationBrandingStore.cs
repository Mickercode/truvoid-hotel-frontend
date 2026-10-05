using Npgsql;

namespace TruvoID.Infrastructure.Postgres;

public sealed record OrganizationBranding(
    Guid OrganizationId,
    string? WorkspaceName,
    string PrimaryColor,
    string AccentColor,
    string? WelcomeMessage,
    string? LogoContentType,
    byte[]? LogoContent);

public sealed class OrganizationBrandingStore(NpgsqlDataSource dataSource)
{
    public async Task<OrganizationBranding> GetAsync(Guid organizationId, CancellationToken ct = default)
    {
        await EnsureAsync(organizationId, ct);
        await using var command = dataSource.CreateCommand("""
            SELECT organization_id, workspace_name, primary_color, accent_color,
                   welcome_message, logo_content_type, logo_content
            FROM control.organization_branding
            WHERE organization_id = @organizationId
            """);
        command.Parameters.AddWithValue("organizationId", organizationId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }

    public async Task SaveAsync(Guid organizationId, string? workspaceName, string primaryColor, string accentColor, string? welcomeMessage, CancellationToken ct = default)
    {
        ValidateColor(primaryColor); ValidateColor(accentColor);
        await EnsureAsync(organizationId, ct);
        await using var command = dataSource.CreateCommand("""
            UPDATE control.organization_branding
            SET workspace_name = @workspaceName, primary_color = @primaryColor,
                accent_color = @accentColor, welcome_message = @welcomeMessage, updated_at = now()
            WHERE organization_id = @organizationId
            """);
        command.Parameters.AddWithValue("organizationId", organizationId);
        command.Parameters.AddWithValue("workspaceName", (object?)workspaceName?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("primaryColor", primaryColor);
        command.Parameters.AddWithValue("accentColor", accentColor);
        command.Parameters.AddWithValue("welcomeMessage", (object?)welcomeMessage?.Trim() ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveLogoAsync(Guid organizationId, string contentType, byte[] content, CancellationToken ct = default)
    {
        if (content.Length == 0 || content.Length > 2 * 1024 * 1024)
            throw new ArgumentException("Logo must be between 1 byte and 2 MB.");
        if (contentType is not ("image/png" or "image/jpeg" or "image/webp"))
            throw new ArgumentException("Logo must be PNG, JPEG, or WebP.");
        await EnsureAsync(organizationId, ct);
        await using var command = dataSource.CreateCommand("""
            UPDATE control.organization_branding
            SET logo_content_type = @contentType, logo_content = @content, updated_at = now()
            WHERE organization_id = @organizationId
            """);
        command.Parameters.AddWithValue("organizationId", organizationId);
        command.Parameters.AddWithValue("contentType", contentType);
        command.Parameters.AddWithValue("content", content);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureAsync(Guid organizationId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("INSERT INTO control.organization_branding (organization_id) VALUES (@organizationId) ON CONFLICT (organization_id) DO NOTHING");
        command.Parameters.AddWithValue("organizationId", organizationId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static OrganizationBranding Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetFieldValue<byte[]>(6));

    private static void ValidateColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 7 || value[0] != '#' || !value.Skip(1).All(Uri.IsHexDigit))
            throw new ArgumentException("Colors must be six-digit hex values.");
    }
}
