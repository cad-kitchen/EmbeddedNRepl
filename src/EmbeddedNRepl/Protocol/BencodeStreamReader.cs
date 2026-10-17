using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbeddedNRepl.Hosting;

namespace EmbeddedNRepl.Protocol;

/// <summary>Owns and closes its stream on EOF, cancellation, disposal, or framing failure. One pending read is allowed.</summary>
public sealed class BencodeStreamReader : IDisposable
{
    private readonly Stream stream;
    private readonly BencodeDecoder decoder;
    private readonly IReplClock clock;
    private readonly TimeSpan partialTimeout;
    private readonly byte[] buffer = new byte[8192];
    private int offset;
    private int buffered;
    private int reading;
    private int closed;
    private TimeSpan? partialStarted;
    private TimeSpan bufferReceivedAt;

    public BencodeStreamReader(Stream stream, ReplLimits limits, IReplClock? clock = null)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        decoder = new BencodeDecoder(limits);
        partialTimeout = limits.PartialFrameTimeout;
        this.clock = clock ?? new ReplClock();
    }

    public async Task<BencodeFrame?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref reading, 1, 0) != 0)
            throw new InvalidOperationException("A read is already in progress.");
        try
        {
            if (Volatile.Read(ref closed) != 0)
                throw new ObjectDisposedException(nameof(BencodeStreamReader));
            using (cancellationToken.Register(Dispose))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CheckDeadline();
                    if (buffered == 0)
                    {
                        buffered = await FillBuffer(cancellationToken).ConfigureAwait(false);
                        bufferReceivedAt = clock.Elapsed;
                        offset = 0;
                        CheckDeadline();
                        if (buffered == 0)
                        {
                            decoder.Complete();
                            Dispose();
                            return null;
                        }
                    }
                    if (partialStarted == null)
                        partialStarted = bufferReceivedAt;
                    var frame = decoder.Read(buffer, offset, buffered, out int consumed);
                    offset += consumed;
                    buffered -= consumed;
                    if (frame != null)
                    {
                        partialStarted = null;
                        return frame;
                    }
                    CheckDeadline();
                }
            }
        }
        catch
        {
            Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        finally { Volatile.Write(ref reading, 0); }
    }

    private void CheckDeadline()
    {
        if (partialStarted.HasValue && clock.Elapsed - partialStarted.Value >= partialTimeout)
            throw new BencodeException("The absolute partial-frame deadline expired.");
    }

    private async Task<int> FillBuffer(CancellationToken cancellationToken)
    {
        if (!partialStarted.HasValue)
            return await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
        using (var timerCancellation = new CancellationTokenSource())
        {
            TimeSpan remaining = partialTimeout - (clock.Elapsed - partialStarted.Value);
            if (remaining <= TimeSpan.Zero)
                throw new BencodeException("The absolute partial-frame deadline expired.");
            Task timer = clock.Delay(remaining, timerCancellation.Token);
            try
            {
                Task<int> read = stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                if (await Task.WhenAny(read, timer).ConfigureAwait(false) != read)
                {
                    Dispose();
                    // Closing the owned stream aborts Framework NetworkStream reads too.
                    // Observe the original read before returning; no orphaned handler remains.
                    try
                    {
                        await read.ConfigureAwait(false);
                    }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                    catch (OperationCanceledException) { }
                    throw new BencodeException("The absolute partial-frame deadline expired.");
                }
                return await read.ConfigureAwait(false);
            }
            finally
            {
                timerCancellation.Cancel();
                try
                {
                    await timer.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref closed, 1) == 0)
            stream.Dispose();
    }
}
