namespace TruvoID.API.Endpoints;

/// <summary>
/// Links emailed to users must point at the dashboard app, which is not always on
/// the apex domain — set App__BaseUrl (e.g. the Vercel URL) until it is.
/// </summary>
internal static class AppLinks
{
    public static string Build(IConfiguration configuration, string pathAndQuery) =>
        $"{(configuration["App:BaseUrl"] ?? "https://gettruvoid.com").TrimEnd('/')}/{pathAndQuery.TrimStart('/')}";
}
