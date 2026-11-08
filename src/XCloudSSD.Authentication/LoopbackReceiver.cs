using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;

namespace XCloudSSD.Authentication;

public sealed record AuthorizationResult(string Code, string Verifier, string RedirectUri);

/// <summary>
/// Desktop side of the browser sign-in: opens the user's default browser on the provider's
/// official sign-in page and waits for the redirect on http://127.0.0.1:{random port}/.
/// No passwords or cookies are seen by XCloudSSD.
/// </summary>
public static class LoopbackReceiver
{
    public static async Task<AuthorizationResult> SignInWithGoogleAsync(string clientId, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirectUri = $"http://127.0.0.1:{port}/";
            var (verifier, challenge) = GoogleOAuth.CreatePkce();
            var state = GoogleOAuth.CreateState();
            OpenBrowser(GoogleOAuth.BuildAuthorizeUrl(clientId, redirectUri, challenge, state));

            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                await using var stream = client.GetStream();
                var requestLine = await ReadRequestLineAsync(stream, ct).ConfigureAwait(false);
                // "GET /?state=...&code=... HTTP/1.1"
                var parts = requestLine.Split(' ');
                if (parts.Length < 2 || !parts[1].StartsWith("/?", StringComparison.Ordinal))
                {
                    await RespondAsync(stream, 404, "Not found", ct).ConfigureAwait(false);
                    continue;
                }
                var query = HttpUtility.ParseQueryString(parts[1][2..]);
                if (query["state"] != state)
                {
                    await RespondAsync(stream, 400, "State mismatch. Please try again from XCloudSSD.", ct).ConfigureAwait(false);
                    throw new InvalidOperationException("OAuth state mismatch.");
                }
                if (query["error"] is { } err)
                {
                    await RespondAsync(stream, 200, "Sign-in was cancelled. You can close this tab.", ct).ConfigureAwait(false);
                    throw new OperationCanceledException("Sign-in failed: " + err);
                }
                var code = query["code"];
                if (string.IsNullOrEmpty(code))
                {
                    await RespondAsync(stream, 400, "Missing authorization code.", ct).ConfigureAwait(false);
                    continue;
                }
                await RespondAsync(stream, 200, "XCloudSSD: sign-in complete. You can close this tab and return to the app.", ct).ConfigureAwait(false);
                return new AuthorizationResult(code, verifier, redirectUri);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    public static void OpenBrowser(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static async Task<string> ReadRequestLineAsync(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[8192];
        var sb = new StringBuilder();
        while (sb.Length < 16384)
        {
            var n = await s.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n == 0) break;
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            var text = sb.ToString();
            var idx = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (idx >= 0 && text.Contains("\r\n\r\n", StringComparison.Ordinal)) return text[..idx];
        }
        var all = sb.ToString();
        var end = all.IndexOf("\r\n", StringComparison.Ordinal);
        return end >= 0 ? all[..end] : all;
    }

    private static async Task RespondAsync(NetworkStream s, int status, string message, CancellationToken ct)
    {
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>XCloudSSD</title></head>" +
                   $"<body style=\"font-family:Segoe UI,sans-serif;background:#111;color:#eee;padding:48px\">" +
                   $"<h2>XCloudSSD</h2><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(head, ct).ConfigureAwait(false);
        await s.WriteAsync(body, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }
}
