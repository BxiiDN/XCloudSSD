using System.Text;

namespace XCloudSSD.Core;

public static class HttpContentText
{
    /// <summary>
    /// Reads a response body as UTF-8 regardless of the declared charset. Some servers (TeraBox) send
    /// non-standard names such as "utf8", which makes HttpContent.ReadAsStringAsync throw
    /// "The character set provided in ContentType is invalid".
    /// </summary>
    public static async Task<string> ReadUtf8Async(this HttpContent content, CancellationToken ct)
    {
        var bytes = await content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }
}
