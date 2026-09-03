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
