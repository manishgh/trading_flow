using System.Security.Cryptography;
using System.Text.Json;

namespace TradingFlow.Domain.News;

/// <summary>Compares exact serializer output without retaining paired full-document strings or byte arrays.</summary>
public static class CompanyNewsAssessmentContentComparer
{
    /// <summary>Preserves the caller's serializer options, including default options used by extraction replay.</summary>
    public static bool SameContent<T>(T left, T right, JsonSerializerOptions? options = null) =>
        Fingerprint(left, options) == Fingerprint(right, options);

    /// <summary>Hashes every serialized byte and its count while keeping only serializer buffers in memory.</summary>
    public static (string Sha256, long ByteCount) Fingerprint<T>(T value, JsonSerializerOptions? options = null)
    {
        using var destination = new HashingWriteStream();
        JsonSerializer.Serialize(destination, value, options);
        return (destination.FinishHash(), destination.Length);
    }

    private sealed class HashingWriteStream : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long byteCount;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => byteCount;
        public override long Position { get => byteCount; set => throw new NotSupportedException(); }

        /// <summary>Accepts serializer buffers directly without copying their bytes into retained storage.</summary>
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        /// <summary>Accumulates the exact wire digest and byte count for the current serialization.</summary>
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hash.AppendData(buffer);
            byteCount = checked(byteCount + buffer.Length);
        }

        /// <summary>Returns the completed digest only after serialization has flushed its final buffer.</summary>
        public string FinishHash() => Convert.ToHexStringLower(hash.GetHashAndReset());

        /// <summary>No bytes are buffered by this sink; the serializer owns its buffers.</summary>
        public override void Flush() { }

        /// <summary>Releases the per-comparison cryptographic state.</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing) hash.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>The digest sink cannot expose serialized bytes for reading.</summary>
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>A forward-only digest cannot seek or replay prior bytes.</summary>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <summary>The serialized byte count cannot be changed independently of the digest.</summary>
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
