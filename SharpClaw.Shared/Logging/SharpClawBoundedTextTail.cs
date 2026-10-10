using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SharpClaw.Shared.DurableStorage;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;
using MsLogger = Microsoft.Extensions.Logging.ILogger;

namespace SharpClaw.Shared.Logging;


public sealed class SharpClawBoundedTextTail
{
    private readonly int _maximumBytes;
    private readonly Queue<string> _lines = [];
    private readonly Lock _gate = new();
    private int _encodedBytes;

    public SharpClawBoundedTextTail(int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        _maximumBytes = maximumBytes;
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _lines.Count;
        }
    }

    public int EncodedBytes
    {
        get
        {
            lock (_gate)
                return _encodedBytes;
        }
    }

    public void AppendLine(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var separatorBytes = Encoding.UTF8.GetByteCount(Environment.NewLine);
        var contentLimit = Math.Max(0, _maximumBytes - separatorBytes);
        var bounded = SharpClawLogBounds.TruncateUtf8(value, contentLimit, out _);
        var lineBytes = Encoding.UTF8.GetByteCount(bounded) + separatorBytes;
        if (lineBytes > _maximumBytes)
            return;

        lock (_gate)
        {
            while (_lines.Count > 0 && _encodedBytes + lineBytes > _maximumBytes)
            {
                var removed = _lines.Dequeue();
                _encodedBytes -= Encoding.UTF8.GetByteCount(removed) + separatorBytes;
            }

            _lines.Enqueue(bounded);
            _encodedBytes += lineBytes;
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
            return [.. _lines];
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            _encodedBytes = 0;
        }
    }
}
