using Soenneker.Queues.Intrusive.Abstractions;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Queues.Intrusive.ValueMpsc.Tests;

public sealed class ReclaimingQueueRaceTests
{
    [Test]
    public async Task Queue_fits_stub_inside_existing_cache_line_padding()
    {
        await Assert.That(Unsafe.SizeOf<ValueIntrusiveMpscReclaimingQueue<HookNode>>()).IsEqualTo(72);
        await Assert.That(Unsafe.SizeOf<ValueIntrusiveMpscQueue<HookNode>>()).IsEqualTo(72);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Producer_winning_last_node_race_preserves_linked_successor(bool startAtStub)
    {
        var stub = new HookNode();
        var first = new HookNode();
        var last = new HookNode();
        var successor = new HookNode();
        var queue = new ValueIntrusiveMpscReclaimingQueue<HookNode>(stub);
        if (!startAtStub)
            queue.Enqueue(first);
        queue.Enqueue(last);
        if (!startAtStub && !queue.TryDequeue(out _))
            throw new InvalidOperationException("Missing first node.");

        // The consumer has read tail == last when it clears the stub's link.
        // Complete a producer at this point so that the consumer's CAS loses.
        Action publish = () => queue.Enqueue(successor);
        stub.OnNext = startAtStub ? () => stub.OnNext = publish : publish;
        bool gotLast = queue.TryDequeue(out HookNode actualLast);
        last.Next = null;
        bool gotSuccessor = queue.TryDequeue(out HookNode actualSuccessor);

        await Assert.That(gotLast).IsTrue();
        await Assert.That(actualLast).IsSameReferenceAs(last);
        await Assert.That(gotSuccessor).IsTrue();
        await Assert.That(actualSuccessor).IsSameReferenceAs(successor);
        await Assert.That(queue.IsEmpty()).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Producer_winning_last_node_race_must_publish_before_head_is_reclaimed(bool startAtStub)
    {
        var stub = new HookNode();
        var first = new HookNode();
        var last = new HookNode();
        var successor = new HookNode();
        var queue = new ValueIntrusiveMpscReclaimingQueue<HookNode>(stub);
        if (!startAtStub)
            queue.Enqueue(first);
        queue.Enqueue(last);
        if (!startAtStub && !queue.TryDequeue(out _))
            throw new InvalidOperationException("Missing first node.");

        using var tailExchanged = new ManualResetEventSlim();
        using var publishLink = new ManualResetEventSlim();
        Task producer = Task.CompletedTask;
        Action publish = () =>
        {
            last.OnNext = () =>
            {
                tailExchanged.Set();
                if (!publishLink.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Consumer did not release the producer.");
            };
            producer = Task.Run(() => queue.Enqueue(successor));
            if (!tailExchanged.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Producer did not exchange the tail.");
        };
        stub.OnNext = startAtStub ? () => stub.OnNext = publish : publish;

        bool dequeued;
        HookNode unavailable;
        bool empty;
        try
        {
            dequeued = queue.TryDequeue(out unavailable);
            empty = queue.IsEmpty();
        }
        finally
        {
            publishLink.Set();
            await producer;
        }

        await Assert.That(dequeued).IsFalse();
        await Assert.That(unavailable).IsNull();
        await Assert.That(empty).IsFalse();
        bool gotLast = queue.TryDequeueSpinUntilLinked(out HookNode actualLast);
        last.Next = null;
        bool gotSuccessor = queue.TryDequeueSpinUntilLinked(out HookNode actualSuccessor);
        await Assert.That(gotLast).IsTrue();
        await Assert.That(actualLast).IsSameReferenceAs(last);
        await Assert.That(gotSuccessor).IsTrue();
        await Assert.That(actualSuccessor).IsSameReferenceAs(successor);
        await Assert.That(queue.IsEmpty()).IsTrue();
    }

    [Test]
    public async Task Concurrent_producers_can_recycle_dequeued_nodes_immediately()
    {
        const int producerCount = 4;
        const int iterations = 10_000;
        var queue = new ValueIntrusiveMpscReclaimingQueue<ReusableNode>(new ReusableNode(-1));
        var returned = new ReusableNode?[producerCount];
        var lastSequence = Enumerable.Repeat(-1, producerCount).ToArray();
        using var start = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Task[] producers = Enumerable.Range(0, producerCount).Select(id => Task.Run(() =>
        {
            var node = new ReusableNode(id);
            start.Wait(timeout.Token);
            for (int sequence = 0; sequence < iterations; sequence++)
            {
                node.Sequence = sequence;
                queue.Enqueue(node);
                var spin = new SpinWait();
                while ((node = Volatile.Read(ref returned[id])) is null)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    spin.SpinOnce(-1);
                }
                returned[id] = null;
            }
        })).ToArray();

        Task consumer = Task.Run(() =>
        {
            start.Wait(timeout.Token);
            var spin = new SpinWait();
            for (int consumed = 0; consumed < producerCount * iterations;)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (!queue.TryDequeue(out ReusableNode node))
                {
                    spin.SpinOnce(-1);
                    continue;
                }

                if (node.Sequence != ++lastSequence[node.Producer])
                    throw new InvalidOperationException("Duplicate or out-of-order node.");
                node.Next = null;
                Volatile.Write(ref returned[node.Producer], node);
                consumed++;
                spin.Reset();
            }
        });

        start.Set();
        await Task.WhenAll(producers.Append(consumer));
        await Assert.That(queue.IsEmpty()).IsTrue();
    }

    private sealed class ReusableNode(int producer) : IntrusiveNode<ReusableNode>
    {
        internal int Producer { get; } = producer;
        internal int Sequence;
    }

    // Intercept a single link access to force a precise producer/consumer interleaving
    // without changing the queue implementation or relying on timing.
    private sealed class HookNode : IIntrusiveNode<HookNode>
    {
        private HookNode? _next;
        internal Action? OnNext;

        public ref HookNode? Next
        {
            get
            {
                Interlocked.Exchange(ref OnNext, null)?.Invoke();
                return ref _next;
            }
        }
    }
}
