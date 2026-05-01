using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using System.Text;

namespace Segra.Backend.App
{
    internal sealed class TrimmingFileSink : ILogEventSink, IDisposable
    {
        private readonly string _path;
        private readonly long _maxBytes;
        private readonly long _trimTargetBytes;
        private readonly ITextFormatter _formatter;
        private readonly object _lock = new();
        private FileStream _stream = null!;
        private StreamWriter _writer = null!;
        private long _bytesWritten;

        public TrimmingFileSink(string path, long maxBytes, long trimTargetBytes, string outputTemplate)
        {
            _path = path;
            _maxBytes = maxBytes;
            _trimTargetBytes = trimTargetBytes;
            _formatter = new MessageTemplateTextFormatter(outputTemplate);
            Open();
        }

        private void Open()
        {
            _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _bytesWritten = _stream.Length;
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_lock)
            {
                using var sw = new StringWriter();
                _formatter.Format(logEvent, sw);
                var text = sw.ToString();

                _writer.Write(text);
                _writer.Flush();
                _bytesWritten += Encoding.UTF8.GetByteCount(text);

                if (_bytesWritten >= _maxBytes)
                {
                    Trim();
                }
            }
        }

        private void Trim()
        {
            _writer.Dispose();
            _stream.Dispose();

            try
            {
                var bytes = File.ReadAllBytes(_path);
                var bytesToDrop = bytes.LongLength - _trimTargetBytes;

                if (bytesToDrop > 0)
                {
                    // Round up to the next newline so we don't bisect a log line
                    int start = (int)bytesToDrop;
                    while (start < bytes.Length && bytes[start] != (byte)'\n') start++;
                    if (start < bytes.Length) start++;

                    File.WriteAllBytes(_path, bytes[start..]);
                }
            }
            finally
            {
                Open();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _writer?.Dispose();
                _stream?.Dispose();
            }
        }
    }
}
