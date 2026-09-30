using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace EverLogger.Core.Data;

/// <summary>
/// A thread-safe bounded queue for DataPackets.
/// </summary>
public class ConcurrentDataQueue
{
    private readonly Channel<DataPacket> _channel;

    /// <summary>
    /// Gets the current number of items in the queue.
    /// </summary>
    public int Count => _channel.Reader.Count;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrentDataQueue"/> class.
    /// </summary>
    /// <param name="capacity">The maximum number of items the queue can hold. Default is 10000.</param>
    public ConcurrentDataQueue(int capacity = 10000)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = false
        };
        _channel = Channel.CreateBounded<DataPacket>(options);
    }

    /// <summary>
    /// Writes a packet to the queue. Drops oldest if full (handled by Channel).
    /// </summary>
    /// <param name="packet">The packet to write.</param>
    /// <returns>True if the packet was written successfully.</returns>
    public bool TryWrite(DataPacket packet)
    {
        return _channel.Writer.TryWrite(packet);
    }

    /// <summary>
    /// Attempts to read a packet without blocking.
    /// </summary>
    /// <param name="packet">The read packet, if available.</param>
    /// <returns>True if a packet was read; false if the queue is empty.</returns>
    public bool TryRead(out DataPacket packet)
    {
        return _channel.Reader.TryRead(out packet);
    }

    /// <summary>
    /// Reads all available packets asynchronously.
    /// </summary>
    /// <param name="ct">A cancellation token to observe.</param>
    /// <returns>An async enumerable of DataPackets.</returns>
    public IAsyncEnumerable<DataPacket> ReadAllAsync(CancellationToken ct = default)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }

    /// <summary>
    /// Reads a single packet asynchronously.
    /// </summary>
    /// <param name="ct">A cancellation token to observe.</param>
    /// <returns>A ValueTask representing the asynchronous read operation.</returns>
    public ValueTask<DataPacket> ReadAsync(CancellationToken ct = default)
    {
        return _channel.Reader.ReadAsync(ct);
    }
}
