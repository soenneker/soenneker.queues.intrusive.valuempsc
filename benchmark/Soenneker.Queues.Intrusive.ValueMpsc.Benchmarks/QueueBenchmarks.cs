using BenchmarkDotNet.Attributes;
using Soenneker.Queues.Intrusive.Abstractions;
using System;

namespace Soenneker.Queues.Intrusive.ValueMpsc.Benchmarks;

[MemoryDiagnoser]
public class QueueBenchmarks
{
    private ValueIntrusiveMpscReclaimingQueue<Node> _reclaiming;
    private ValueIntrusiveMpscQueue<Node> _moving;
    private Node[] _reclaimingNodes = null!;
    private Node[] _movingNodes = null!;

    [Params(1, 8, 64)]
    public int BatchSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _reclaiming = new(new Node());
        _moving = new(new Node());
        _reclaimingNodes = new Node[BatchSize];
        _movingNodes = new Node[BatchSize];
        for (int i = 0; i < BatchSize; i++)
        {
            _reclaimingNodes[i] = new Node();
            _movingNodes[i] = new Node();
        }
    }

    [Benchmark]
    public void ReclaimingRoundTrip()
    {
        foreach (Node node in _reclaimingNodes)
            _reclaiming.Enqueue(node);

        for (int i = 0; i < BatchSize; i++)
        {
            if (!_reclaiming.TryDequeueSpinUntilLinked(out _))
                throw new InvalidOperationException("Missing node.");
        }
    }

    [Benchmark]
    public void MovingRoundTrip()
    {
        foreach (Node node in _movingNodes)
            _moving.Enqueue(node);

        for (int i = 0; i < BatchSize; i++)
        {
            // Only the previous head can be reused on the next invocation.
            Node released = _moving.Head;
            if (!_moving.TryDequeueSpinUntilLinked(out _))
                throw new InvalidOperationException("Missing node.");
            _movingNodes[i] = released;
        }
    }

    public sealed class Node : IntrusiveNode<Node>;
}
