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

    /// <summary>
    /// As <see cref="ReadLimitedAsync"/>, for a body whose room has been taken (<see cref="DavParkingBudget"/>): with a
    /// <c>Content-Length</c>, into an array of that length, so what is held is what was declared and is kept as it is;
    /// without one, into chunks that grow with the bytes received and are copied once into an array of their length at
    /// the end. So no buffer doubles, and none is copied but a chunked body's: for the moment of that copy, it holds its
    /// chunks and the array, up to twice the <see cref="DavRules.ParkingCap"/> it took.
    /// </summary>
    public static async Task<byte[]?> ReadSizedAsync(HttpRequest request, long limit, CancellationToken ct)
    {
        if (request.ContentLength > limit) return null;
        if (request.ContentLength is { } length)
        {
            var bytes = new byte[length];
            var filled = 0;
            int read;
            while (filled < bytes.Length && (read = await request.Body.ReadAsync(bytes.AsMemory(filled), ct)) > 0) filled += read;
            // Kestrel holds a body to its length; this is for a server that doesn't.
            if (filled < bytes.Length) return bytes[..filled];
            return await request.Body.ReadAsync(new byte[1], ct) > 0 ? null : bytes;
        }

        var chunks = new List<byte[]>();
        long total = 0;
        while (true)
        {
            // Below the large object heap's 85,000 bytes, as ReadLimitedAsync's chunk.
            var chunk = new byte[81920];
            var used = 0;
            int read;
            while (used < chunk.Length && (read = await request.Body.ReadAsync(chunk.AsMemory(used), ct)) > 0) used += read;
            if ((total += used) > limit) return null;
            if (used > 0) chunks.Add(used == chunk.Length ? chunk : chunk[..used]);
            if (used < chunk.Length) break;
        }

        var all = new byte[total];
        var at = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(all, at);
            at += chunk.Length;
        }

        return all;
    }

    /// <summary>Whether the request comes with a body: a length above zero, or one sent in chunks.</summary>
    public static bool Has(HttpRequest request) => request.ContentLength > 0 || (request.ContentLength is null && request.Headers.TransferEncoding.Count > 0);
}
