using System.Security.Cryptography;

// ═══════════════════════════════════════════════════════════════════════
// HashingStream — read-through tee that feeds every byte the caller pulls
// into an <see cref="IncrementalHash"/>. Used by the upload endpoint so
// SHA256 is computed in the same pass that uploads to blob storage; no
// CryptoStream lifecycle (FlushFinalBlock-twice on Dispose) gotchas.
// ═══════════════════════════════════════════════════════════════════════

public sealed class HashingStream : Stream
{
    readonly Stream _inner;
    readonly IncrementalHash _hash;
    readonly bool _leaveInnerOpen;

    public HashingStream(Stream inner, IncrementalHash hash, bool leaveInnerOpen = false)
    {
        _inner = inner;
        _hash = hash;
        _leaveInnerOpen = leaveInnerOpen;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Azure SDK's PartitionedUploader calls Stream.Read synchronously
        // for its internal buffering, but Kestrel forbids sync IO on the
        // request body by default. Bridge to ReadAsync so the inner async
        // pipe is what actually executes.
        return ReadAsync(buffer.AsMemory(offset, count), default).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await _inner.ReadAsync(buffer, ct);
        if (n > 0) _hash.AppendData(buffer.Span[..n]);
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveInnerOpen) _inner.Dispose();
        base.Dispose(disposing);
    }
}
