// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Dav;

/// <summary>A request's bytes, held to a limit by counting them as they arrive, so a chunked body without a declared
/// length is held to it as one with a length is (brief open point 6).</summary>
public static class DavBody
{
    /// <summary>The whole body, or null if it is longer than <paramref name="limit"/>; empty if there is none.</summary>
    public static async Task<byte[]?> ReadLimitedAsync(HttpRequest request, long limit, CancellationToken ct)
    {
        if (request.ContentLength > limit) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Whether the request comes with a body: a length above zero, or one sent in chunks.</summary>
    public static bool Has(HttpRequest request) => request.ContentLength > 0 || (request.ContentLength is null && request.Headers.TransferEncoding.Count > 0);
}
