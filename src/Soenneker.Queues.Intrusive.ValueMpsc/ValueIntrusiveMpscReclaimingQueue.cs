using Soenneker.Queues.Intrusive.Abstractions;
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Soenneker.Queues.Intrusive.ValueMpsc;

/// <summary>
/// An intrusive multi-producer, single-consumer queue that releases each dequeued node for immediate reclamation.
/// </summary>
/// <typeparam name="TNode">The intrusive reference-type node stored in the queue.</typeparam>
/// <remarks>
/// This queue retains a permanent stub internally. Unlike <see cref="ValueIntrusiveMpscQueue{TNode}"/>, a successfully
/// dequeued node is no longer queue-owned and may immediately be cleared, pooled, or enqueued again. Multiple producers
/// may enqueue concurrently, but exactly one consumer may call dequeue methods or <see cref="IsEmpty"/>.
///
/// This is a mutable value type. Store and use one stable instance; do not copy it or pass it by value. The stub passed to
/// the constructor remains queue-owned for the lifetime of the queue and must never be enqueued by a caller.
/// </remarks>
public struct ValueIntrusiveMpscReclaimingQueue<TNode> where TNode : class, IIntrusiveNode<TNode>
{
    // Co-locate the consumer-read stub with the consumer head. The producer path only touches Tail.
    private TNode? _stub;
    private CacheLineSeparatedReferences _state;

    /// <summary>
    /// Initializes the queue with a permanent stub node.
    /// </summary>
    /// <param name="stub">The node retained as the queue's permanent internal stub.</param>
    public ValueIntrusiveMpscReclaimingQueue(TNode stub)
    {
        ArgumentNullException.ThrowIfNull(stub);

        _state = default;
        _stub = stub;
        stub.Next = null;
        _state.Head = stub;
        _state.Tail = stub;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNotInitialized()
        => throw new InvalidOperationException("Queue is not initialized. Use the stub constructor.");

    /// <summary>
    /// Enqueues a node. This method is safe to call concurrently from multiple producer threads.
    /// </summary>
    /// <param name="node">An unlinked node that is not the queue's permanent stub.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enqueue(TNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (_state.Tail is null)
            ThrowNotInitialized();

        EnqueueCore(node);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnqueueCore(TNode node)
    {
        node.Next = null;
        TNode previous = (TNode) Interlocked.Exchange(ref _state.Tail, node)!;
        Volatile.Write(ref previous.Next, node);
    }

    /// <summary>
    /// Attempts to dequeue a node without spinning.
    /// </summary>
    /// <param name="node">The dequeued, immediately reclaimable node when successful.</param>
    /// <returns><c>true</c> when a node was dequeued; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// A return value of <c>false</c> may mean either that the queue is empty or that a producer is between publishing
    /// the new tail and linking it from the previous tail.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out TNode node)
    {
        TNode? stub = _stub;
        if (stub is null)
            ThrowNotInitialized();

        return TryDequeueCore(stub!, out node);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryDequeueCore(TNode stub, out TNode node)
    {
        TNode head = (TNode) _state.Head!;
        TNode? next = Volatile.Read(ref head.Next);

        if (ReferenceEquals(head, stub))
        {
            if (next is null)
            {
                node = null!;
                return false;
            }

            _state.Head = next;
            head = next;
            next = Volatile.Read(ref head.Next);
        }

        if (next is not null)
        {
            _state.Head = next;
            node = head;
            return true;
        }

        if (!ReferenceEquals(head, Volatile.Read(ref _state.Tail)))
        {
            node = null!;
            return false;
        }

        EnqueueCore(stub);
        next = Volatile.Read(ref head.Next);

        if (next is null)
        {
            node = null!;
            return false;
        }

        _state.Head = next;
        node = head;
        return true;
    }

    /// <summary>
    /// Attempts to dequeue a node, spinning until an in-progress producer publishes its link.
    /// </summary>
    /// <param name="node">The dequeued, immediately reclaimable node when successful.</param>
    /// <returns><c>true</c> when a node was dequeued; otherwise, <c>false</c> when the queue is empty.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueSpinUntilLinked(out TNode node)
    {
        TNode? stub = _stub;
        if (stub is null)
            ThrowNotInitialized();

        if (TryDequeueCore(stub!, out node))
            return true;

        return TryDequeueSpinUntilLinkedSlow(stub!, out node);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryDequeueSpinUntilLinkedSlow(TNode stub, out TNode node)
    {
        var spinner = new SpinWait();

        while (!IsEmptyCore(stub))
        {
            spinner.SpinOnce();

            if (TryDequeueCore(stub, out node))
                return true;
        }

        node = null!;
        return false;
    }

    /// <summary>
    /// Determines whether the queue is empty.
    /// </summary>
    /// <returns><c>true</c> when the queue contains no user nodes; otherwise, <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEmpty()
    {
        TNode? stub = _stub;
        if (stub is null)
            ThrowNotInitialized();

        return IsEmptyCore(stub!);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsEmptyCore(TNode stub)
    {
        TNode head = (TNode) _state.Head!;
        return ReferenceEquals(head, stub) && Volatile.Read(ref head.Next) is null &&
               ReferenceEquals(head, Volatile.Read(ref _state.Tail));
    }
}
