using Soenneker.Queues.Intrusive.Abstractions;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Queues.Intrusive.ValueMpsc.Tests;

public sealed class ValueIntrusiveMpscReclaimingQueueTests
{
    [Test]
    public async Task Dequeued_node_can_be_immediately_reenqueued()
    {
        var stub = new TestNode(-1);
        var node = new TestNode(1);
        var queue = new ValueIntrusiveMpscReclaimingQueue<TestNode>(stub);

        TestNode current = node;

        for (var i = 0; i < 100_000; i++)
        {
            queue.Enqueue(current);

            if (!queue.TryDequeueSpinUntilLinked(out current))
                throw new InvalidOperationException("The enqueued node was not available.");
        }

        await Assert.That(current).IsSameReferenceAs(node);
        await Assert.That(queue.IsEmpty()).IsTrue();
    }

    [Test]
    public async Task Concurrent_producers_preserve_uniqueness()
    {
        const int producerCount = 4;
        const int nodesPerProducer = 10_000;
        const int total = producerCount * nodesPerProducer;
        var queue = new ValueIntrusiveMpscReclaimingQueue<TestNode>(new TestNode(-1));
        var observed = new ConcurrentDictionary<int, byte>();
        var lastByProducer = Enumerable.Repeat(-1, producerCount).ToArray();
        using var start = new ManualResetEventSlim();

        Task[] producers = Enumerable.Range(0, producerCount)
                                     .Select(producer => Task.Run(() =>
                                     {
                                         start.Wait();
                                         for (var i = 0; i < nodesPerProducer; i++)
                                             queue.Enqueue(new TestNode(producer * nodesPerProducer + i));
                                     }))
                                     .ToArray();

        Task consumer = Task.Run(() =>
        {
            start.Wait();
            var spin = new SpinWait();

            while (observed.Count < total)
            {
                if (queue.TryDequeueSpinUntilLinked(out TestNode node))
                {
                    if (!observed.TryAdd(node.Id, 0))
                        throw new InvalidOperationException($"Duplicate node {node.Id}.");

                    int producer = node.Id / nodesPerProducer;
                    int sequence = node.Id % nodesPerProducer;

                    if (sequence != lastByProducer[producer] + 1)
                        throw new InvalidOperationException($"Producer {producer} was observed out of order at {sequence}.");

                    lastByProducer[producer] = sequence;

                    spin.Reset();
                }
                else
                {
                    spin.SpinOnce();
                }
            }
        });

        start.Set();
        await Task.WhenAll(producers.Append(consumer)).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(observed.Count).IsEqualTo(total);
        await Assert.That(queue.IsEmpty()).IsTrue();
    }

    [Test]
    public void Default_queue_rejects_operations()
    {
        ValueIntrusiveMpscReclaimingQueue<TestNode> uninitialized = default;
        Assert.ThrowsExactly<InvalidOperationException>(() => uninitialized.Enqueue(new TestNode(1)));
        Assert.ThrowsExactly<InvalidOperationException>(() => uninitialized.TryDequeue(out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => uninitialized.TryDequeueSpinUntilLinked(out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => uninitialized.IsEmpty());

    }

    private sealed class TestNode(int id) : IntrusiveNode<TestNode>
    {
        internal int Id { get; } = id;
    }
}
