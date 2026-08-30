using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Soenneker.Queues.Intrusive.Abstractions;

namespace Soenneker.Queues.Intrusive.ValueMpsc;

/// <summary>
/// An intrusive multi-producer, single-consumer (MPSC) queue.
///
/// The queue starts with a dummy node and advances that dummy head as nodes are consumed.
/// Nodes carry their own linkage via <see cref="IIntrusiveNode{TNode}"/>, avoiding allocations,
/// and each enqueue performs one atomic operation.
///
/// Thread-safety:
/// - Multiple producers may call <see cref="Enqueue"/> concurrently.
/// - Exactly one consumer may call the dequeue methods, <see cref="Drain"/>,
///   <see cref="Head"/>, or <see cref="IsEmpty"/>.
/// </summary>
/// <typeparam name="TNode">
/// The node type stored in the queue. It must be a reference type implementing
/// <see cref="IIntrusiveNode{TNode}"/>.
/// </typeparam>
/// <remarks>
/// This is a mutable value type. It must be stored and used as a single instance.
/// Do not copy this struct (for example, by passing it by value).
///
/// A successfully returned node becomes the new consumer head and remains queue-owned until
/// a later successful dequeue releases it. Do not modify its link or enqueue it again while it
/// is the current <see cref="Head"/>.
/// </remarks>
public struct ValueIntrusiveMpscQueue<TNode> where TNode : class, IIntrusiveNode<TNode>
{
    // Consumer-owned moving dummy head.
    private TNode? _head;

    // Producer-shared tail pointer.
    private TNode? _tail;

    /// <summary>
    /// Initializes the queue with an initial dummy node.
    /// </summary>
    /// <param name="stub">
    /// The initial dummy node. It becomes releasable when the first node is successfully dequeued.
    /// </param>
    public ValueIntrusiveMpscQueue(TNode stub)
    {
        ArgumentNullException.ThrowIfNull(stub);

        stub.Next = null;
        _head = stub;
        _tail = stub;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNotInitialized()
        => throw new InvalidOperationException("Queue is not initialized. Use the stub constructor.");

    /// <summary>
    /// Enqueues a node. This method is safe to call concurrently from multiple producer threads.
    /// </summary>
    /// <param name="node">The unlinked node to enqueue.</param>
    /// <remarks>
    /// The node must not already be linked in any intrusive structure and must not be this queue's
    /// current <see cref="Head"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enqueue(TNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        // Producers only need the producer-owned field to validate initialization; do not add a
        // dependency on consumer state to every enqueue.
        if (_tail is null)
            ThrowNotInitialized();

        // Clear linkage before publication to avoid stale chains on reuse.
        node.Next = null;

        // Atomically swap the tail and then publish the link from the previous tail.
        TNode previous = Interlocked.Exchange(ref _tail, node)!;
        Volatile.Write(ref previous.Next, node);
    }

    /// <summary>
    /// Attempts to dequeue a node without spinning.
    /// </summary>
    /// <param name="node">The dequeued node when successful.</param>
    /// <returns><c>true</c> when a node was dequeued; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// A return value of <c>false</c> may mean either that the queue is empty or that a producer
    /// has advanced the tail but has not yet published the link. On success, the returned node is
    /// the new <see cref="Head"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out TNode node)
    {
        // The fast consumer path does not need a load from producer state.
        if (_head is null)
            ThrowNotInitialized();

        return TryDequeueCore(out node);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryDequeueCore(out TNode node)
    {
        TNode head = _head!;
        TNode? next = Volatile.Read(ref head.Next);

        if (next is null)
        {
            node = null!;
            return false;
        }

        _head = next;
        node = next;
        return true;
    }

    /// <summary>
    /// Attempts to dequeue a node, spinning up to <paramref name="maxSpins"/> only when a producer
    /// has advanced the tail but has not yet published the link.
    /// </summary>
    /// <param name="node">The dequeued node when successful.</param>
    /// <param name="maxSpins">The maximum number of spins while waiting for an in-progress enqueue to publish its link.</param>
    /// <returns><c>true</c> when a node was dequeued; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// This does not wait for a new enqueue when the queue is empty. On success, the returned node
    /// becomes the current <see cref="Head"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueSpin(out TNode node, int maxSpins)
    {
        TNode? head = _head;
        if (head is null)
            ThrowNotInitialized();

        TNode? next = Volatile.Read(ref head!.Next);
        if (next is null)
            return TryDequeueSpinSlow(head, out node, maxSpins);

        _head = next;
        node = next;
        return true;
    }

    // Keep the uncommon empty/link-window path out of callers that inline TryDequeueSpin.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryDequeueSpinSlow(TNode head, out TNode node, int maxSpins)
    {
        if (ReferenceEquals(head, Volatile.Read(ref _tail)))
        {
            node = null!;
            return false;
        }

        if (maxSpins <= 0)
        {
            node = null!;
            return false;
        }

        TNode? next = null;
        var spinner = new SpinWait();

        for (var i = 0; i < maxSpins; i++)
        {
            spinner.SpinOnce();
            next = Volatile.Read(ref head.Next);

            if (next is not null)
                break;
        }

        if (next is null)
        {
            node = null!;
            return false;
        }

        _head = next;
        node = next;
        return true;
    }

    /// <summary>
    /// Attempts to dequeue a node, spinning until an in-progress producer publishes its link.
    /// </summary>
    /// <param name="node">The dequeued node when successful.</param>
    /// <returns><c>true</c> when a node was dequeued; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// This returns immediately when the queue is empty and does not wait for a future enqueue.
    /// On success, the returned node becomes the current <see cref="Head"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueSpinUntilLinked(out TNode node)
    {
        TNode? head = _head;
        if (head is null)
            ThrowNotInitialized();

        TNode? next = Volatile.Read(ref head!.Next);
        if (next is null)
            return TryDequeueSpinUntilLinkedSlow(head, out node);

        _head = next;
        node = next;
        return true;
    }

    // Keep the uncommon empty/link-window path out of callers that inline the linked-node path.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryDequeueSpinUntilLinkedSlow(TNode head, out TNode node)
    {
        if (ReferenceEquals(head, Volatile.Read(ref _tail)))
        {
            node = null!;
            return false;
        }

        TNode? next;
        var spinner = new SpinWait();

        do
        {
            spinner.SpinOnce();
            next = Volatile.Read(ref head.Next);
        }
        while (next is null);

        _head = next;
        node = next;
        return true;
    }

    /// <summary>
    /// Gets the current consumer head node.
    /// </summary>
    /// <remarks>
    /// Consumer-thread only. The current head is queue-owned and must not be recycled or relinked.
    /// A consumer that needs delayed reclamation may capture this value before a dequeue; after a
    /// successful dequeue, that previously captured head is no longer owned by the queue.
    /// </remarks>
    public TNode Head
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            TNode? head = _head;
            if (head is null)
                ThrowNotInitialized();

            return head!;
        }
    }

    /// <summary>
    /// Processes up to <paramref name="max"/> currently linked nodes.
    /// </summary>
    /// <param name="action">The action to invoke for each dequeued node.</param>
    /// <param name="max">The maximum number of nodes to process.</param>
    /// <returns>The number of nodes processed.</returns>
    /// <remarks>
    /// The action must not modify a processed node's intrusive link or immediately recycle/re-enqueue
    /// it: each returned node remains the queue's moving dummy head until the next successful advance.
    /// </remarks>
    public int Drain(Action<TNode> action, int max = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfNegative(max);

        if (_head is null)
            ThrowNotInitialized();

        var count = 0;

        // Initialization is invariant after construction, so validate once rather than once per node.
        while (count < max && TryDequeueCore(out TNode next))
        {
            action(next);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Determines whether the queue is currently empty.
    /// </summary>
    /// <returns><c>true</c> if the queue is currently empty; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// Consumer-thread only. A producer in the exchange/link publication window makes this return
    /// <c>false</c>, even though a non-spinning dequeue may not observe the link yet.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEmpty()
    {
        TNode? head = _head;
        if (head is null)
            ThrowNotInitialized();

        return Volatile.Read(ref head!.Next) is null
            && ReferenceEquals(head, Volatile.Read(ref _tail));
    }
}
