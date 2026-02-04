[![](https://img.shields.io/nuget/v/soenneker.queues.intrusive.valuempsc.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.queues.intrusive.valuempsc/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.queues.intrusive.valuempsc/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.queues.intrusive.valuempsc/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.queues.intrusive.valuempsc.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.queues.intrusive.valuempsc/)

# ![](https://user-images.githubusercontent.com/4441470/224455560-91ed3ee7-f510-4041-a8d2-3fc093025112.png) Soenneker.Queues.Intrusive.ValueMpsc
### A zero-allocation, high-performance intrusive MPSC queue using value-based state

---

## Installation

```bash
dotnet add package Soenneker.Queues.Intrusive.ValueMpsc
```

---

## Overview

`ValueIntrusiveMpscQueue<TNode>` is a **multi-producer / single-consumer (MPSC)** queue built around a classic intrusive algorithm with a permanent sentinel (“stub”) node.

This *value* variant is designed to minimize indirection and memory traffic by storing queue state directly in value fields rather than reference wrappers.

Key characteristics:

* Multiple producers may enqueue concurrently.
* Exactly one consumer may dequeue.
* Each enqueue performs **a single atomic operation**.
* **No allocations** are performed by the queue.
* Node linkage is stored directly on the node (intrusive).
* Queue state is held in value types for maximum locality and predictability.

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

### Create a queue with a permanent sentinel

```csharp
var stub = new WorkItem();
var queue = new ValueIntrusiveMpscQueue<WorkItem>(stub);
```

The stub node must remain alive for the entire lifetime of the queue.

---

### Enqueue (multi-producer)

```csharp
queue.Enqueue(new WorkItem { Id = 42 });
```

This operation is lock-free and safe to call concurrently from multiple threads.

---

### Dequeue (single-consumer)

```csharp
if (queue.TryDequeue(out var item))
{
    // process item
}
```

If stronger dequeue guarantees are required (for example, when a producer has advanced the tail but not yet published the link), use:

```csharp
queue.TryDequeueSpin(out var item);
```

---

## Correctness and constraints

This type intentionally enforces strict usage rules:

* **Exactly one consumer thread** is supported.
* A node must not be enqueued more than once at a time.
* Nodes may be reused only after being dequeued.
* The sentinel (stub) node must remain alive for the queue’s lifetime.
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