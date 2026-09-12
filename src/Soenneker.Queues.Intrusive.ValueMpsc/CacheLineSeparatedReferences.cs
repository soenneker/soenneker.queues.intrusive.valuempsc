using System.Runtime.InteropServices;

namespace Soenneker.Queues.Intrusive.ValueMpsc;

[StructLayout(LayoutKind.Explicit, Size = 72)]
internal struct CacheLineSeparatedReferences
{
    [FieldOffset(0)]
    internal object? Head;

    [FieldOffset(64)]
    internal object? Tail;
}

[StructLayout(LayoutKind.Explicit, Size = 72)]
internal struct CacheLineSeparatedReclaimingReferences
{
    [FieldOffset(0)]
    internal object? Head;

    // Use the existing padding for the consumer-only stub instead of growing the queue.
    [FieldOffset(8)]
    internal object? Stub;

    [FieldOffset(64)]
    internal object? Tail;
}
