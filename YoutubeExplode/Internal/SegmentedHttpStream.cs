using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace YoutubeExplode.Internal
{
    internal class SegmentedHttpStream : Stream
    {
        private readonly HttpClient _httpClient;
        private readonly string _url;
        private readonly long _segmentSize;
        private readonly int _retryLimit;

        private Stream? _currentStream;
        private long _position;

        public SegmentedHttpStream(HttpClient httpClient, string url, long length, long segmentSize, int retryLimit = 5)
        {
            _url = url;
            _httpClient = httpClient;
            Length = length;
            _segmentSize = segmentSize;
            _retryLimit = retryLimit;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length { get; }

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0)
                    throw new IOException("An attempt was made to move the position before the beginning of the stream.");

                if (_position == value)
                    return;

                _position = value;
                ClearCurrentStream();
            }
        }

        private void ClearCurrentStream()
        {
            _currentStream?.Dispose();
            _currentStream = null;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // If full length has been exceeded - return 0
            if (Position >= Length)
                return 0;

            int retryCount = 0;

            // Try to read until got something or retry limit has been exceeded
            while (true)
            {
                // If current stream is not set - resolve it
                if (_currentStream == null)
                {
                    _currentStream = await _httpClient.GetStreamAsync(_url, Position, Position + _segmentSize - 1).ConfigureAwait(false);
                }

                try
                {
                    // Read from current stream
                    var bytesRead = await _currentStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);

                    if (bytesRead > 0)
                    {
                        // Advance the position (using field directly to avoid clearing stream)
                        _position += bytesRead;
                        return bytesRead;
                    }
                }
                catch (IOException)
                {
                    // Rethow if reach retry limit
                    if (retryCount++ > _retryLimit)
                    {
                        throw;
                    }
                }

                // If no bytes have been read - resolve a new stream
                ClearCurrentStream();
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        private long GetNewPosition(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    return offset;
                case SeekOrigin.Current:
                    return Position + offset;
                case SeekOrigin.End:
                    return Length + offset;
                default:
                    throw new ArgumentOutOfRangeException(nameof(origin));
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = GetNewPosition(offset, origin);

        #region Not supported

        public override void Flush() => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        #endregion

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
                ClearCurrentStream();
        }
    }
}