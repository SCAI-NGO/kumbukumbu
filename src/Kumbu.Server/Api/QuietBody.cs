// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Api;

/// <summary>
/// A large upload's body: ended after <paramref name="silence"/> without a byte, with an <see cref="IOException"/>,
/// which the routes answer as a body cut off.
/// </summary>
internal sealed class QuietBody(Stream inner, TimeSpan silence, TimeProvider time) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var quiet = new CancellationTokenSource(silence, time);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quiet.Token);
        try
        {
            return await inner.ReadAsync(buffer, both.Token);
        }
        catch (OperationCanceledException) when (quiet.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"No byte of the upload arrived for {silence}.");
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
