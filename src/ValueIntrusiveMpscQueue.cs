using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Soenneker.Queues.Intrusive.Abstractions;

namespace Soenneker.Queues.Intrusive.ValueMpsc;

/// <summary>
/// An intrusive multi-producer, single-consumer (MPSC) queue.
///
/// This queue uses a permanent sentinel ("stub") node and a single atomic operation per enqueue.
/// Nodes carry their own linkage via <see cref="IIntrusiveNode{TNode}"/>, avoiding allocations.
///
/// Thread-safety:
/// - Multiple producers may call <see cref="Enqueue"/> concurrently.
/// - Exactly one consumer may call <see cref="TryDequeue"/>,
///   <see cref="TryDequeueSpinUntilLinked"/>, or <see cref="IsEmpty"/>.
/// </summary>
/// <typeparam name="TNode">
/// The node type stored in the queue. Must be a reference type implementing
/// <see cref="IIntrusiveNode{TNode}"/> and must not be enqueued concurrently or more than once at a time.
/// </typeparam>
/// <remarks>
/// This is a mutable value type. It must be stored and used as a single instance.
/// Do not copy this struct (for example, by passing it by value).
/// </remarks>
public struct ValueIntrusiveMpscQueue<TNode> where TNode : class, IIntrusiveNode<TNode>
{
    // Consumer-owned head pointer (initially the stub).
    private TNode? _head;

    // Producer-shared tail pointer.
    private TNode? _tail;

    /// <summary>
    /// Initializes a new <see cref="ValueIntrusiveMpscQueue{TNode}"/> using the provided stub node.
    /// </summary>
    /// <param name="stub">
    /// A permanent sentinel node that remains allocated for the lifetime of the queue.
    /// Its <see cref="IIntrusiveNode{TNode}.Next"/> reference must initially be <c>null</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="stub"/> is <c>null</c>.
    /// </exception>
    public ValueIntrusiveMpscQueue(TNode stub)
    {
        if (stub is null)
            throw new ArgumentNullException(nameof(stub));

        stub.Next = null;

        _head = stub;
        _tail = stub;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfNotInitialized()
    {
        if (_head is null || _tail is null) ThrowNotInitialized();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNotInitialized()
        => throw new InvalidOperationException("Queue is not initialized. Use the stub constructor.");

    /// <summary>
    /// Enqueues a node into the queue.
    ///
    /// This method is safe to call concurrently from multiple producer threads.
    /// Exactly one atomic operation is performed per enqueue.
    /// </summary>
    /// <param name="node">The node to enqueue.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="node"/> is <c>null</c>.
    /// </exception>
    /// <remarks>
    /// The provided node must not already be enqueued in this or any other queue.
    /// Node reuse is allowed only after the node has been dequeued by the consumer.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void Enqueue(TNode node)
    {
        if (node is null)
            throw new ArgumentNullException(nameof(node));

        ThrowIfNotInitialized();

        // Clear linkage before publication to avoid stale chains on reuse.
        node.Next = null;

        // Atomically swap the tail and link the previous tail to this node.
        TNode prev = Interlocked.Exchange(ref _tail!, node);
        Volatile.Write(ref prev.Next, node);
    }

    /// <summary>
    /// Attempts to dequeue a node from the queue without spinning.
    ///
    /// This method must be called by the single consumer thread only.
    /// </summary>
    /// <param name="node">
    /// When this method returns <c>true</c>, contains the dequeued node.
    /// When this method returns <c>false</c>, contains <c>null</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if a node was successfully dequeued; otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// A return value of <c>false</c> does not necessarily mean the queue is empty.
    /// It may also indicate that a producer has advanced the tail pointer but has not yet
    /// published the link to the next node.
    ///
    /// If stronger dequeue semantics are required, use <see cref="TryDequeueSpin"/>
    /// or <see cref="TryDequeueSpinUntilLinked"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool TryDequeue(out TNode node)
    {
        ThrowIfNotInitialized();

        TNode head = _head!;
        TNode? next = Volatile.Read(ref head.Next);

        if (next is null)
        {
            if (ReferenceEquals(head, Volatile.Read(ref _tail!)))
            {
                node = null!;
                return false;
            }

            node = null!;
            return false;
        }

        _head = next;
        node = next;
        return true;
    }

    /// <summary>
    /// Attempts to dequeue a node from the queue, spinning only in the producer link-publish window.
    /// </summary>
    /// <remarks>
    /// If the queue is truly empty, this returns <c>false</c>.
    /// If a producer has advanced the tail pointer but has not yet published the link from the current head,
    /// this method spins until the link is observed and then dequeues the node.
    ///
    /// This method does not wait for producers to enqueue new nodes.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool TryDequeueSpinUntilLinked(out TNode node)
    {
        TNode head = _head!;
        TNode? next = Volatile.Read(ref head.Next);

        if (next is null)
        {
            if (ReferenceEquals(head, Volatile.Read(ref _tail!)))
            {
                node = null!;
                return false;
            }

            var sw = new SpinWait();
            do
            {
                sw.SpinOnce();
                next = Volatile.Read(ref head.Next);
            }
            while (next is null);
        }

        _head = next!;
        node = next!;
        return true;
    }

    /// <summary>
    /// Gets the current consumer head node.
    /// </summary>
    /// <remarks>
    /// Consumer-thread only. The returned node is typically the permanent stub or the most
    /// recently dequeued node and may be used for recycling or cleanup logic.
    /// </remarks>
    public TNode Head
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfNotInitialized();
            return _head!;
        }
    }

    /// <summary>
    /// Processes up to the specified number of nodes by invoking the provided action for each dequeued node.
    /// </summary>
    /// <remarks>Throws an exception if the queue is not initialized. Processing stops if the queue becomes
    /// empty before reaching the specified maximum.</remarks>
    /// <param name="action">The action to perform on each node that is dequeued from the queue. This delegate is called once for each node
    /// processed.</param>
    /// <param name="max">The maximum number of nodes to process. Must be a non-negative integer. If not specified, all available nodes
    /// are processed.</param>
    /// <returns>The number of nodes that were processed by the action. This value will be less than or equal to the specified
    /// maximum.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Drain(Action<TNode> action, int max = int.MaxValue)
    {
        ThrowIfNotInitialized();
        var count = 0;

        while (count < max && TryDequeue(out TNode n))
        {
            action(n);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Determines whether the queue is currently empty.
    /// </summary>
    /// <remarks>
    /// Consumer-thread only. This is a best-effort check and may transiently return
    /// <c>true</c> while a producer is mid-enqueue (tail advanced but link not yet published).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEmpty()
    {
        ThrowIfNotInitialized();

        TNode head = _head!;
        return Volatile.Read(ref head.Next) is null
            && ReferenceEquals(head, Volatile.Read(ref _tail!));
    }
}
