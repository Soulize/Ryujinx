using System;

namespace Ryujinx.Graphics.GAL
{
    public readonly struct BufferRange : IEquatable<BufferRange>
    {
        private static readonly BufferRange _empty = new BufferRange(BufferHandle.Null, 0, 0);

        public static BufferRange Empty => _empty;

        public BufferHandle Handle { get; }

        public int Offset { get; }
        public int Size   { get; }

        public BufferRange(BufferHandle handle, int offset, int size)
        {
            Handle = handle;
            Offset = offset;
            Size   = size;
        }

        public bool Equals(BufferRange other)
        {
            return Handle == other.Handle && Offset == other.Offset && Size == other.Size;
        }
    }
}