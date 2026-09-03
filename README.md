[![](https://img.shields.io/nuget/v/soenneker.queues.intrusive.valuempsc.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.queues.intrusive.valuempsc/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.queues.intrusive.valuempsc/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.queues.intrusive.valuempsc/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.queues.intrusive.valuempsc.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.queues.intrusive.valuempsc/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.queues.intrusive.valuempsc/build-and-test.yml?label=build%20and%20test&style=for-the-badge)](https://github.com/soenneker/soenneker.queues.intrusive.valuempsc/actions/workflows/build-and-test.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.queues.intrusive.valuempsc/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.queues.intrusive.valuempsc/actions/workflows/codeql.yml)

# ![](https://user-images.githubusercontent.com/4441470/224455560-91ed3ee7-f510-4041-a8d2-3fc093025112.png) Soenneker.Queues.Intrusive.ValueMpsc
### A zero-allocation, high-performance intrusive MPSC queue using value-based state

---

## Installation

```bash
dotnet add package Soenneker.Queues.Intrusive.ValueMpsc
```

---

## Overview

`ValueIntrusiveMpscQueue<TNode>` is a **multi-producer / single-consumer (MPSC)** queue built around an intrusive moving-dummy algorithm. It starts with a stub node; after each successful dequeue, the returned node becomes the next dummy head.

`ValueIntrusiveMpscReclaimingQueue<TNode>` uses a permanent internal stub instead. A successfully dequeued node is immediately released from the queue, so consumers can clear, pool, or re-enqueue it without waiting for another dequeue. This variant is useful for pooled async waiters and other workloads where delayed moving-dummy reclamation would require an additional cross-thread handshake.

This value variant stores the queue state directly in a mutable struct. Keep one instance in a field and never copy it or pass it by value: a copy would create a second consumer state over the same producer chain.

The producer tail is placed 64 bytes after the consumer head. This makes the queue state 72 bytes on the supported runtime, trading a small amount of embedded state for lower cache-coherency traffic under concurrent use.

Key characteristics:

* Multiple producers may enqueue concurrently.
* Exactly one consumer may dequeue.
* Each enqueue performs **a single atomic operation**.
* **No allocations** are performed by the queue.
* Node linkage is stored directly on the node (intrusive).
* The consumer fast path performs no atomic read-modify-write operation.
* Producer and consumer state are cache-line separated to avoid false sharing.
* Queue state can be embedded directly in another type.

This makes it especially suitable for **hot paths** in low-level concurrency primitives.

Choose the reclaiming variant when immediate node reuse matters. Its dequeue path occasionally re-enqueues the permanent stub, while the moving-dummy variant has the smaller steady-state dequeue algorithm.

---

## Usage

### Define a node type

Nodes must implement `IIntrusiveNode<TNode>` or derive from `IntrusiveNode<TNode>`.

```csharp
public sealed class WorkItem : IntrusiveNode<WorkItem>
{
    public int Id;
}
```

Each node carries its own linkage; the queue never allocates or wraps nodes.

---

### Create a queue with an initial dummy node

```csharp
var stub = new WorkItem();
var queue = new ValueIntrusiveMpscQueue<WorkItem>(stub);
```

The queue keeps the current dummy alive. The original stub is released by the first successful dequeue and can then be reclaimed by the consumer.

---

### Enqueue (multi-producer)

```csharp
queue.Enqueue(new WorkItem { Id = 42 });
```

This operation is lock-free and safe to call concurrently from multiple threads.

---

### Dequeue (single-consumer)

```csharp
var released = queue.Head;

if (queue.TryDequeue(out var item))
{
    // Process item without changing item.Next.
    // "released" is the old dummy and is now safe to reclaim or relink.
}
```

The returned `item` is also the queue's new `Head`. Its payload can be processed immediately, but the node itself cannot be recycled, relinked, or re-enqueued until a later successful dequeue releases it.

If stronger dequeue guarantees are required (for example, when a producer has advanced the tail but not yet published the link), use:

```csharp
queue.TryDequeueSpin(out var item, maxSpins: 16);
```

---

## Correctness and constraints

This type intentionally enforces strict usage rules:

* **Exactly one consumer thread** is supported.
* A node must not be enqueued more than once at a time.
* A node returned by a dequeue remains queue-owned as the moving dummy head.
* Only the previous `Head` is released by a successful dequeue and safe to reuse.
* A node must not be recycled, relinked, or re-enqueued while it is the current `Head`.
* `TryDequeue` may return `false` while a producer is mid-enqueue — this is expected.

Violating these constraints will result in undefined behavior.

This is a **low-level primitive**, not a general-purpose collection.

---

## When to use this

This queue is a good fit when:

* You are building synchronization primitives (async locks, semaphores, schedulers).
* Allocation-free behavior is mandatory.
* You need tight control over memory ordering and visibility.
* You can enforce a single-consumer contract.
* You can keep the mutable queue struct in one stable storage location.

If you need a general-purpose queue with multiple consumers, prefer `ConcurrentQueue<T>` or `System.Threading.Channels`.
