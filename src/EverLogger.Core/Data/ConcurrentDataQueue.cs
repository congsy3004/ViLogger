using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace EverLogger.Core.Data;

/// <summary>
/// A thread-safe, unbounded FIFO queue for DataPackets (many writers, one reader).
/// </summary>
/// <remarks>
/// Unbounded on purpose: this queue feeds the log writer, and the log must never drop data.
/// If the disk is temporarily slower than the serial port, packets wait here (in memory)
/// instead of being discarded.
/// </remarks>
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
    public ConcurrentDataQueue()
    {
        _channel = Channel.CreateUnbounded<DataPacket>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true
        });
    }

    /// <summary>
    /// Writes a packet to the queue. Never drops data.
    /// </summary>
    /// <param name="packet">The packet to write.</param>
    /// <returns>True if the packet was accepted; false only after <see cref="Complete"/>.</returns>
    public bool TryWrite(DataPacket packet)
    {
        return _channel.Writer.TryWrite(packet);
    }

    /// <summary>
    /// Stops accepting new packets. Packets already accepted can still be read.
    /// </summary>
    public void Complete()
    {
        _channel.Writer.TryComplete();
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
