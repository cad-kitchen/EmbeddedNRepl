using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbeddedNRepl.Testing;

public sealed class ControlledInputStream : Stream
{
    private readonly object gate = new object();
    private readonly Queue<byte> bytes = new Queue<byte>();
    private readonly SemaphoreSlim reads = new SemaphoreSlim(0);
    private TaskCompletionSource<int>? pending;
    private byte[]? target;
    private int targetOffset;
    private int targetCount;
    public bool Closed
    {
        get; private set;
    }
    public Action? OnRead
    {
        get; set;
    }
    public Task WaitForRead() => TestDeadline.Complete(reads.WaitAsync());
    public void Push(byte[] input)
    {
        TaskCompletionSource<int>? completion = null;
        int count = 0;
        lock (gate)
        {
            foreach (byte value in input)
                bytes.Enqueue(value);
            if (pending != null)
            {
                count = Copy(target!, targetOffset, targetCount);
                completion = pending;
                pending = null;
            }
        }
        completion?.TrySetResult(count);
    }
    private int Copy(byte[] buffer, int offset, int count)
    {
        int copied = 0;
        while (copied < count && bytes.Count > 0)
            buffer[offset + copied++] = bytes.Dequeue();
        return copied;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (Closed)
                throw new ObjectDisposedException(nameof(ControlledInputStream));
            if (pending != null)
                throw new InvalidOperationException("Only one read is supported.");
            OnRead?.Invoke();
            reads.Release();
            if (bytes.Count > 0)
                return Task.FromResult(Copy(buffer, offset, count));
            target = buffer;
            targetOffset = offset;
            targetCount = count;
            pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            return pending.Task;
        }
    }
    protected override void Dispose(bool disposing)
    {
        TaskCompletionSource<int>? completion;
        lock (gate)
        {
            Closed = true;
            completion = pending;
            pending = null;
            target = null;
            bytes.Clear();
        }
        completion?.TrySetException(new ObjectDisposedException(nameof(ControlledInputStream)));
        base.Dispose(disposing);
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException(); set => throw new NotSupportedException();
    }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
