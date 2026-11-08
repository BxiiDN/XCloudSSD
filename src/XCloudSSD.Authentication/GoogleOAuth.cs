using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XCloudSSD.Core;

namespace XCloudSSD.Authentication;

/// <summary>
/// Google OAuth 2.0 for installed apps: authorization code flow with PKCE and a loopback
/// redirect (https://developers.google.com/identity/protocols/oauth2/native-app).
/// </summary>
public static class GoogleOAuth
{
    public const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    /// <summary>Full Drive access is required to read/write arbitrary files mounted as a disk.</summary>
    public const string DriveScope = "https://www.googleapis.com/auth/drive";

    public static (string Verifier, string Challenge) CreatePkce()
    {
        var bytes = RandomNumberGenerator.GetBytes(48);
        var verifier = Base64Url(bytes);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    public static string BuildAuthorizeUrl(string clientId, string redirectUri, string challenge, string state)
    {
        var q = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = DriveScope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline",
            // Forces a refresh token for every added account, and lets the user pick a different account.
            ["prompt"] = "consent select_account",
        };
        return AuthorizeEndpoint + "?" + string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    private static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class TokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
}

/// <summary>Talks to Google's token endpoint. Used by the service only.</summary>
public sealed class GoogleTokenClient
{
    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _clientSecret;

    public GoogleTokenClient(HttpClient http, string clientId, string clientSecret)
    {
        _http = http;
        _clientId = clientId;
        _clientSecret = clientSecret;
    }

    public Task<TokenResponse> ExchangeCodeAsync(string code, string verifier, string redirectUri, CancellationToken ct)
        => PostAsync(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier,
        }, ct);

    public Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken ct)
        => PostAsync(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        }, ct);

    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
        using var resp = await _http.PostAsync(GoogleOAuth.RevokeEndpoint, content, ct).ConfigureAwait(false);
        // 400 means already revoked/expired: fine for "disconnect".
    }

    private async Task<TokenResponse> PostAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            resp = await _http.PostAsync(GoogleOAuth.TokenEndpoint, content, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new CloudException(CloudErrorKind.Offline, "Cannot reach Google sign-in: " + ex.Message, null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CloudException(CloudErrorKind.Timeout, "Google sign-in timed out.", null, ex);
        }

        using (resp)
        {
            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadFromJsonAsync<TokenResponse>(ct).ConfigureAwait(false)
                       ?? throw new CloudException(CloudErrorKind.Unknown, "Empty token response.");

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string error = "";
            try
            {
                using var doc = JsonDocument.Parse(body);
                error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            }
            catch (JsonException)
            {
            }
            if (error is "invalid_grant" or "unauthorized_client" || resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new CloudException(CloudErrorKind.Unauthorized, $"Google rejected the credentials ({error}). Sign in again.", (int)resp.StatusCode);
            if ((int)resp.StatusCode >= 500)
                throw new CloudException(CloudErrorKind.Transient, $"Google token endpoint error {(int)resp.StatusCode}.", (int)resp.StatusCode);
            throw new CloudException(CloudErrorKind.Unknown, $"Token request failed: {(int)resp.StatusCode} {error}", (int)resp.StatusCode);
        }
    }
}

/// <summary>
/// Supplies a valid access token for one account, refreshing it automatically before expiry
/// and persisting rotated refresh tokens in the encrypted <see cref="Security.SecretStore"/>.
/// </summary>
public sealed class GoogleAccessTokenSource
{
    private readonly GoogleTokenClient _client;
    private readonly Security.SecretStore _store;
    private readonly string _accountId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAt;
    private string _refreshToken;

    public GoogleAccessTokenSource(GoogleTokenClient client, Security.SecretStore store, string accountId, string refreshToken)
    {
        _client = client;
        _store = store;
        _accountId = accountId;
        _refreshToken = refreshToken;
    }

    public void Seed(string accessToken, int expiresIn)
    {
        _accessToken = accessToken;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, expiresIn - 60));
    }

    public async Task<string> GetAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (!forceRefresh && _accessToken is not null && DateTimeOffset.UtcNow < _expiresAt) return _accessToken;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _accessToken is not null && DateTimeOffset.UtcNow < _expiresAt) return _accessToken;
            var t = await _client.RefreshAsync(_refreshToken, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(t.RefreshToken) && t.RefreshToken != _refreshToken)
            {
                _refreshToken = t.RefreshToken;
                var existing = _store.Load(_accountId) ?? new Dictionary<string, string>();
                existing["refresh_token"] = _refreshToken;
                _store.Save(_accountId, existing);
            }
            Seed(t.AccessToken, t.ExpiresIn);
            return t.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string RefreshToken => _refreshToken;
}
