using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Gpu.Memory
{
    /// <summary>
    /// Memory range used for buffers.
    /// </summary>
    struct BufferBounds
    {
        /// <summary>
        /// Region GPU address.
        /// </summary>
        public ulong GpuAddress { get; }

        /// <summary>
        /// Cached buffer.
        /// </summary>
        public Buffer Buffer { get; private set; }

        /// <sumarry>
        /// Cached buffer unmapped sequence. If this is different from the buffer's, it must be refetched.
        /// </sumarry>
        public int UnmappedSequence { get; private set; }

        /// <summary>
        /// Region virtual address.
        /// </summary>
        public ulong Address { get; }

        /// <summary>
        /// Region size in bytes.
        /// </summary>
        public ulong Size { get; }

        /// <summary>
        /// Buffer usage flags.
        /// </summary>
        public BufferUsageFlags Flags { get; }

        /// <summary>
        /// Creates a new buffer region.
        /// </summary>
        /// <param name="gpuVa">Region virtual address</param>
        /// <param name="address">Region address</param>
        /// <param name="size">Region size</param>
        /// <param name="buffer">Buffer</param>
        /// <param name="flags">Buffer usage flags</param>
        public BufferBounds(ulong gpuVa, ulong address, ulong size, Buffer buffer, BufferUsageFlags flags = BufferUsageFlags.None)
        {
            GpuAddress = gpuVa;
            Address = address;
            Size = size;
            Buffer = buffer;
            UnmappedSequence = buffer?.UnmappedSequence ?? 0;
            Flags = flags;
        }

        public static bool TranslateAndCreateBuffer(MemoryManager memoryManager, ref BufferBounds bounds, ulong gpuVa, ulong size, BufferUsageFlags flags = BufferUsageFlags.None)
        {
            if (gpuVa != bounds.GpuAddress || size != bounds.Size)
            {
                (ulong address, Buffer buffer) = memoryManager.Physical.BufferCache.TranslateAndCreateBuffer(memoryManager, gpuVa, size);

                bounds = new BufferBounds(gpuVa, address, size, buffer, flags);

                return true;
            }
            else if (flags != bounds.Flags)
            {
                bounds = new BufferBounds(bounds.GpuAddress, bounds.Address, bounds.Size, bounds.Buffer, flags);

                return true;
            }

            return false;
        }

        private void RefetchBuffer(BufferCache cache, bool write)
        {
            Buffer = cache.GetBuffer(Address, Size, write);
            UnmappedSequence = Buffer.UnmappedSequence;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SynchronizeMemory(bool write = false)
        {
            Buffer.SynchronizeMemory(Address, Size);

            if (write)
            {
                Buffer.SignalModified(Address, Size);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BufferRange GetBufferRange(BufferCache cache)
        {
            bool write = Flags.HasFlag(BufferUsageFlags.Write);

            if ((Buffer?.UnmappedSequence ?? 0) != UnmappedSequence)
            {
                RefetchBuffer(cache, write);
            }
            else
            {
                SynchronizeMemory(write);
            }

            return Buffer.GetRange(Address, Size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BufferRange GetBufferRangeTillEnd(BufferCache cache)
        {
            bool write = Flags.HasFlag(BufferUsageFlags.Write);

            if ((Buffer?.UnmappedSequence ?? 0) != UnmappedSequence)
            {
                RefetchBuffer(cache, write);
            }
            else
            {
                SynchronizeMemory(write);
            }

            return Buffer.GetRange(Address);
        }
    }
}