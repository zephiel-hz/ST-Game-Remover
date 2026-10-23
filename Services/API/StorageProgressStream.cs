using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SteamPluginManager
{
    internal sealed class StorageProgressStream : Stream
    {
        private readonly Stream _innerStream;
        private readonly long _totalLength;
        private readonly Action<long> _reportProgress;
        private long _totalSent;

        public StorageProgressStream(Stream innerStream, long totalLength, Action<long> reportProgress)
        {
            _innerStream = innerStream;
            _totalLength = totalLength;
            _reportProgress = reportProgress;
        }

        public override bool CanRead => _innerStream.CanRead;
        public override bool CanSeek => _innerStream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _totalLength;
        public override long Position
        {
            get => _innerStream.Position;
            set => _innerStream.Position = value;
        }

        public override void Flush() => _innerStream.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _innerStream.Read(buffer, offset, count);
            if (read > 0)
            {
                _totalSent += read;
                _reportProgress(_totalSent);
            }
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
            if (read > 0)
            {
                _totalSent += read;
                _reportProgress(_totalSent);
            }
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
        public override void SetLength(long value) => _innerStream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _innerStream.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
