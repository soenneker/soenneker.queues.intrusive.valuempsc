[![](https://img.shields.io/nuget/v/soenneker.queues.intrusive.valuempsc.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.queues.intrusive.valuempsc/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.queues.intrusive.valuempsc/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.queues.intrusive.valuempsc/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.queues.intrusive.valuempsc.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.queues.intrusive.valuempsc/)
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

This *value* variant is designed to minimize indirection and memory traffic by storing queue state directly in value fields rather than reference wrappers.

Key characteristics:

* Multiple producers may enqueue concurrently.
* Exactly one consumer may dequeue.
* Each enqueue performs **a single atomic operation**.
* **No allocations** are performed by the queue.
* Node linkage is stored directly on the node (intrusive).
* The consumer fast path performs no atomic read-modify-write operation.
* Queue state is held in a value type for locality and predictable embedding.

This makes it especially suitable for **hot paths** in low-level concurrency primitives.

---

## Why a “Value” MPSC?

Compared to reference-based implementations, this variant:

* Avoids extra object indirection.
* Reduces cache misses in contention-heavy scenarios.
* Plays well with aggressive inlining and AOT scenarios.
* Is easier to embed inside other value-centric primitives.

If you are building performance-critical infrastructure (locks, schedulers, wait queues), this version is usually the right default.

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
* You care about instruction count, cache locality, and predictable latency.

If you need a general-purpose queue with multiple consumers, prefer `ConcurrentQueue<T>` or `System.Threading.Channels`.
