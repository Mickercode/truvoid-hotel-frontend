using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TruvoID.Infrastructure.Identity;

namespace TruvoID.Tests;

public class IdentitySubjectTests
{
    [Theory]
    [InlineData("nin", "12345678901", "12345678901")]
    [InlineData("nin", " 123 4567 8901 ", "12345678901")]
    [InlineData("bvn", "22233344455", "22233344455")]
    [InlineData("phone", "08031234567", "08031234567")]
    [InlineData("phone", "+2348031234567", "08031234567")]
    [InlineData("phone", "2348031234567", "08031234567")]
    [InlineData("phone", "0803-123-4567", "08031234567")]
    [InlineData("phone", "07011234567", "07011234567")]
    public void Normalize_AcceptsAndCanonicalizes(string type, string raw, string expected) =>
        Assert.Equal(expected, IdentitySubject.Normalize(type, raw));

    [Theory]
    [InlineData("nin", "1234567890")]      // 10 digits
    [InlineData("nin", "123456789012")]    // 12 digits
    [InlineData("nin", "1234567890a")]
    [InlineData("bvn", "")]
    [InlineData("phone", "0803123456")]    // too short
    [InlineData("phone", "06031234567")]   // not a mobile prefix
    [InlineData("passport", "A1234567")]
    public void Normalize_RejectsMalformedInput(string type, string raw) =>
        Assert.Throws<ArgumentException>(() => IdentitySubject.Normalize(type, raw));
}

public class SandboxIdentityProviderTests
{
    private readonly SandboxIdentityProvider _sandbox = new(TimeSpan.Zero);

    [Theory]
    [InlineData("nin", "00000000001", IdentityOutcome.Match)]
    [InlineData("nin", "00000000002", IdentityOutcome.NoMatch)]
    [InlineData("nin", "00000000003", IdentityOutcome.ProviderError)]
    [InlineData("bvn", "22222222221", IdentityOutcome.Match)]
    [InlineData("phone", "08000000003", IdentityOutcome.ProviderError)]
    [InlineData("nin", "12345678901", IdentityOutcome.NoMatch)] // a real-looking NIN never "verifies" in sandbox
    public async Task ReturnsDocumentedOutcomes(string type, string number, IdentityOutcome expected)
    {
        var result = await _sandbox.VerifyAsync(type, number, "k", CancellationToken.None);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(expected == IdentityOutcome.Match, result.Identity is not null);
    }

    [Fact]
    public void IsLabelledSandbox() => Assert.Equal("sandbox", _sandbox.Environment);
}

public class IdAccessIdentityProviderTests
{
    private sealed class StubHandler(HttpStatusCode status, string body, Exception? throws = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (throws is not null) throw throws;
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://idaccess.test/v1/") };
    }

    private static (IdAccessIdentityProvider Provider, StubHandler Handler) Create(HttpStatusCode status, string body, Exception? throws = null, string? key = "sk_test")
    {
        var handler = new StubHandler(status, body, throws);
        return (new IdAccessIdentityProvider(new StubFactory(handler), key, NullLogger<IdAccessIdentityProvider>.Instance), handler);
    }

    private const string MatchBody = """
        { "success": true, "data": { "verdict": "MATCH", "data": {
            "first_name": "SADIQ", "middle_name": "MUHAMMAD", "last_name": "ABDULRASHEED",
            "date_of_birth": "20-03-1991", "gender": "m", "phone_number": "07035061222",
            "state_of_origin": "Plateau", "residential_address": "JOS", "photograph": "QUJD" } } }
        """;

    [Fact]
    public async Task Match_ParsesNestedIdentityAndSendsTheRightRequest()
    {
        var (provider, handler) = Create(HttpStatusCode.OK, MatchBody);

        var result = await provider.VerifyAsync("nin", "51407765930", "idem-1", CancellationToken.None);

        Assert.Equal(IdentityOutcome.Match, result.Outcome);
        Assert.Equal("SADIQ MUHAMMAD ABDULRASHEED", result.Identity!.FullName);
        Assert.Equal("Male", result.Identity.Gender);
        Assert.Equal("data:image/jpeg;base64,QUJD", result.Identity.Photo);
        Assert.Equal("https://idaccess.test/v1/identity/nin/advance", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer sk_test", handler.Request.Headers.Authorization!.ToString());
        Assert.Equal("idem-1", handler.Request.Headers.GetValues("Idempotency-Key").Single());
        Assert.Contains("\"nin\":\"51407765930\"", handler.RequestBody);
    }

    [Fact]
    public async Task Phone_UsesPhoneNumberField()
    {
        var (provider, handler) = Create(HttpStatusCode.OK, MatchBody);
        await provider.VerifyAsync("phone", "08031234567", "k", CancellationToken.None);
        Assert.EndsWith("identity/phone/basic", handler.Request!.RequestUri!.ToString());
        Assert.Contains("\"phone_number\":\"08031234567\"", handler.RequestBody);
    }

    [Fact]
    public async Task NonMatchVerdict_IsNoMatchWithoutIdentity()
    {
        var (provider, _) = Create(HttpStatusCode.OK, """{ "success": true, "data": { "verdict": "NO_MATCH" } }""");
        var result = await provider.VerifyAsync("nin", "00000000000", "k", CancellationToken.None);
        Assert.Equal(IdentityOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task ExplicitNotFoundError_IsBillableNoMatch()
    {
        var (provider, _) = Create(HttpStatusCode.NotFound, """{ "success": false, "error": { "message": "NIN not found" } }""");
        Assert.Equal(IdentityOutcome.NoMatch, (await provider.VerifyAsync("nin", "00000000000", "k", CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{ "success": false, "error": { "message": "Upstream down" } }""")]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{ "success": false, "error": { "message": "Invalid payload" } }""")]
    [InlineData(HttpStatusCode.OK, "<html>gateway</html>")]
    [InlineData(HttpStatusCode.OK, """{ "success": false }""")]
    public async Task AmbiguousOrFailedResponses_AreProviderErrors(HttpStatusCode status, string body)
    {
        var (provider, _) = Create(status, body);
        Assert.Equal(IdentityOutcome.ProviderError, (await provider.VerifyAsync("nin", "00000000000", "k", CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task NetworkFailure_IsProviderError()
    {
        var (provider, _) = Create(HttpStatusCode.OK, "", new HttpRequestException("connection refused"));
        Assert.Equal(IdentityOutcome.ProviderError, (await provider.VerifyAsync("nin", "00000000000", "k", CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task MissingApiKey_IsNotConfiguredAndMakesNoCall()
    {
        var (provider, handler) = Create(HttpStatusCode.OK, MatchBody, key: null);
        Assert.False(provider.IsConfigured);
        Assert.Equal(IdentityOutcome.ProviderError, (await provider.VerifyAsync("nin", "00000000000", "k", CancellationToken.None)).Outcome);
        Assert.Null(handler.Request);
    }
}
