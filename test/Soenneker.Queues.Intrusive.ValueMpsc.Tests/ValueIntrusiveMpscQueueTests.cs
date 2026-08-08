using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Queues.Intrusive.Abstractions;
using Soenneker.Tests.Unit;
using TUnit.Assertions.Enums;

namespace Soenneker.Queues.Intrusive.ValueMpsc.Tests;

public sealed class ValueIntrusiveMpscQueueTests : UnitTest
{
    [Test]
    public async Task Constructor_clears_stub_link_and_initializes_empty_queue()
    {
        var stale = new TestNode();
        var stub = new TestNode();
        stub.Next = stale;

        var queue = new ValueIntrusiveMpscQueue<TestNode>(stub);
        TestNode head = queue.Head;
        bool isEmpty = queue.IsEmpty();
        bool dequeued = queue.TryDequeueSpinUntilLinked(out TestNode node);

        await Assert.That(stub.Next).IsNull();
        await Assert.That(head).IsSameReferenceAs(stub);
        await Assert.That(isEmpty).IsTrue();
        await Assert.That(dequeued).IsFalse();
        await Assert.That(node).IsNull();
    }

    [Test]
    public void Constructor_rejects_null_stub()
    {
        Assert.ThrowsExactly<ArgumentNullException>("stub", () => new ValueIntrusiveMpscQueue<TestNode>(null!));
    }

    [Test]
    public async Task Enqueue_and_dequeue_preserve_fifo_while_head_moves_to_last_dequeued_node()
    {
        var stub = new TestNode();
        var first = new TestNode(sequence: 1);
        var second = new TestNode(sequence: 2);
        var third = new TestNode(sequence: 3);
        var fourth = new TestNode(sequence: 4);
        var queue = new ValueIntrusiveMpscQueue<TestNode>(stub);

        queue.Enqueue(first);
        queue.Enqueue(second);
        queue.Enqueue(third);

        TestNode? stubNext = stub.Next;
        TestNode? firstNext = first.Next;
        TestNode? secondNext = second.Next;
        TestNode? thirdNext = third.Next;

        bool gotFirst = queue.TryDequeue(out TestNode actualFirst);
        TestNode headAfterFirst = queue.Head;
        bool gotSecond = queue.TryDequeue(out TestNode actualSecond);
        TestNode headAfterSecond = queue.Head;
        bool gotThird = queue.TryDequeue(out TestNode actualThird);
        TestNode headAfterThird = queue.Head;
        bool emptyAfterThird = queue.IsEmpty();
        bool gotUnexpected = queue.TryDequeue(out TestNode unexpected);

        queue.Enqueue(fourth);
        TestNode? thirdNextAfterEnqueue = third.Next;
        bool gotFourth = queue.TryDequeueSpinUntilLinked(out TestNode actualFourth);
        TestNode finalHead = queue.Head;

        await Assert.That(stubNext).IsSameReferenceAs(first);
        await Assert.That(firstNext).IsSameReferenceAs(second);
        await Assert.That(secondNext).IsSameReferenceAs(third);
        await Assert.That(thirdNext).IsNull();

        await Assert.That(gotFirst).IsTrue();
        await Assert.That(actualFirst).IsSameReferenceAs(first);
        await Assert.That(headAfterFirst).IsSameReferenceAs(first);
        await Assert.That(gotSecond).IsTrue();
        await Assert.That(actualSecond).IsSameReferenceAs(second);
        await Assert.That(headAfterSecond).IsSameReferenceAs(second);
        await Assert.That(gotThird).IsTrue();
        await Assert.That(actualThird).IsSameReferenceAs(third);
        await Assert.That(headAfterThird).IsSameReferenceAs(third);
        await Assert.That(emptyAfterThird).IsTrue();
        await Assert.That(gotUnexpected).IsFalse();
        await Assert.That(unexpected).IsNull();

        await Assert.That(thirdNextAfterEnqueue).IsSameReferenceAs(fourth);
        await Assert.That(gotFourth).IsTrue();
        await Assert.That(actualFourth).IsSameReferenceAs(fourth);
        await Assert.That(finalHead).IsSameReferenceAs(fourth);
    }

    [Test]
    public async Task Successful_dequeue_releases_previous_head_for_relinking()
    {
        var stub = new TestNode();
        var first = new TestNode(sequence: 1);
        var second = new TestNode(sequence: 2);
        var third = new TestNode(sequence: 3);
        var unrelated = new TestNode(sequence: 99);
        var queue = new ValueIntrusiveMpscQueue<TestNode>(stub);
        queue.Enqueue(first);
        queue.Enqueue(second);
        queue.Enqueue(third);

        TestNode oldHead = queue.Head;
        bool gotFirst = queue.TryDequeue(out TestNode actualFirst);
        TestNode newHead = queue.Head;

        oldHead.Next = null;
        TestNode? clearedLink = oldHead.Next;
        oldHead.Next = unrelated;
        TestNode? relinked = oldHead.Next;
        TestNode? currentHeadLink = newHead.Next;

        bool gotSecond = queue.TryDequeue(out TestNode actualSecond);
        bool gotThird = queue.TryDequeue(out TestNode actualThird);
        bool gotUnexpected = queue.TryDequeue(out TestNode unexpected);

        await Assert.That(oldHead).IsSameReferenceAs(stub);
        await Assert.That(gotFirst).IsTrue();
        await Assert.That(actualFirst).IsSameReferenceAs(first);
        await Assert.That(newHead).IsSameReferenceAs(first);
        await Assert.That(clearedLink).IsNull();
        await Assert.That(relinked).IsSameReferenceAs(unrelated);
        await Assert.That(currentHeadLink).IsSameReferenceAs(second);
        await Assert.That(gotSecond).IsTrue();
        await Assert.That(actualSecond).IsSameReferenceAs(second);
        await Assert.That(gotThird).IsTrue();
        await Assert.That(actualThird).IsSameReferenceAs(third);
        await Assert.That(gotUnexpected).IsFalse();
        await Assert.That(unexpected).IsNull();
    }

    [Test]
    public void Default_queue_rejects_every_operation()
    {
        ValueIntrusiveMpscQueue<TestNode> queue = default;

        Assert.ThrowsExactly<InvalidOperationException>(() => queue.Enqueue(new TestNode()));
        Assert.ThrowsExactly<InvalidOperationException>(() => queue.TryDequeue(out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => queue.TryDequeueSpin(out _, 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => queue.TryDequeueSpinUntilLinked(out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = queue.Head);
        Assert.ThrowsExactly<InvalidOperationException>(() => queue.Drain(_ => { }));
        Assert.ThrowsExactly<InvalidOperationException>(() => queue.IsEmpty());
    }

    [Test]
    public async Task Drain_rejects_invalid_arguments_without_consuming_a_node()
    {
        var expected = new TestNode();
        var queue = new ValueIntrusiveMpscQueue<TestNode>(new TestNode());
        queue.Enqueue(expected);

        Assert.ThrowsExactly<ArgumentNullException>("action", () => queue.Drain(null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>("max", () => queue.Drain(_ => { }, -1));

        bool dequeued = queue.TryDequeue(out TestNode actual);
        await Assert.That(dequeued).IsTrue();
        await Assert.That(actual).IsSameReferenceAs(expected);
    }

    [Test]
    public async Task Drain_honors_max_and_processes_nodes_in_fifo_order()
    {
        var queue = new ValueIntrusiveMpscQueue<TestNode>(new TestNode());
        queue.Enqueue(new TestNode(sequence: 1));
        queue.Enqueue(new TestNode(sequence: 2));
        queue.Enqueue(new TestNode(sequence: 3));
        var observed = new List<int>(3);

        int firstCount = queue.Drain(node => observed.Add(node.Sequence), 2);
        bool emptyAfterFirstDrain = queue.IsEmpty();
        int secondCount = queue.Drain(node => observed.Add(node.Sequence));
        bool emptyAfterSecondDrain = queue.IsEmpty();

        await Assert.That(firstCount).IsEqualTo(2);
        await Assert.That(emptyAfterFirstDrain).IsFalse();
        await Assert.That(secondCount).IsEqualTo(1);
        await Assert.That(emptyAfterSecondDrain).IsTrue();
        await Assert.That(observed).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Concurrent_producers_and_single_consumer_preserve_uniqueness_and_each_producers_fifo()
    {
        const int producerCount = 4;
        const int nodesPerProducer = 2_000;
        const int totalNodes = producerCount * nodesPerProducer;

        var holder = new QueueHolder(new TestNode());
        var nodes = new TestNode[producerCount][];
        for (var producer = 0; producer < producerCount; producer++)
        {
            nodes[producer] = new TestNode[nodesPerProducer];
            for (var sequence = 0; sequence < nodesPerProducer; sequence++)
                nodes[producer][sequence] = new TestNode(producer, sequence);
        }

        using var start = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var producers = new Task[producerCount];

        for (var producer = 0; producer < producerCount; producer++)
        {
            int producerIndex = producer;
            producers[producer] = Task.Run(() =>
            {
                start.Wait(timeout.Token);
                foreach (TestNode node in nodes[producerIndex])
                    holder.Queue.Enqueue(node);
            }, timeout.Token);
        }

        var observed = new List<TestNode>(totalNodes);
        Task consumer = Task.Run(() =>
        {
            start.Wait(timeout.Token);
            var spin = new SpinWait();

            while (observed.Count < totalNodes)
            {
                timeout.Token.ThrowIfCancellationRequested();

                if (holder.Queue.TryDequeue(out TestNode node))
                {
                    observed.Add(node);
                    spin.Reset();
                }
                else
                {
                    spin.SpinOnce();
                }
            }
        }, timeout.Token);

        start.Set();
        await Task.WhenAll(producers.Append(consumer));

        await Assert.That(observed.Count).IsEqualTo(totalNodes);
        await Assert.That(observed.Select(node => node.Id).Distinct().Count()).IsEqualTo(totalNodes);

        for (var producer = 0; producer < producerCount; producer++)
        {
            int[] actualSequence = observed.Where(node => node.Producer == producer)
                                           .Select(node => node.Sequence)
                                           .ToArray();
            int[] expectedSequence = Enumerable.Range(0, nodesPerProducer).ToArray();

            await Assert.That(actualSequence).IsEquivalentTo(expectedSequence, CollectionOrdering.Matching);
        }

        await Assert.That(holder.Queue.IsEmpty()).IsTrue();
        await Assert.That(holder.Queue.Head).IsSameReferenceAs(observed[^1]);
    }

    private sealed class QueueHolder(TestNode stub)
    {
        public ValueIntrusiveMpscQueue<TestNode> Queue = new(stub);
    }

    private sealed class TestNode(int producer = -1, int sequence = -1) : IntrusiveNode<TestNode>
    {
        public int Producer { get; } = producer;

        public int Sequence { get; } = sequence;

        public int Id { get; } = producer * 10_000 + sequence;
    }
}
