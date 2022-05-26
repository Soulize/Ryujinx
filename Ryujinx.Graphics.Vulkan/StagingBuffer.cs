using Ryujinx.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Ryujinx.Graphics.Vulkan
{
    struct StagingBufferReserved
    {
        public readonly BufferHolder Buffer;
        public readonly int Offset;
        public readonly int Size;

        public StagingBufferReserved(BufferHolder buffer, int offset, int size)
        {
            Buffer = buffer;
            Offset = offset;
            Size = size;
        }
    }

    class StagingBuffer : IDisposable
    {
        private const int BufferSize = 16 * 1024 * 1024;

        private int _freeOffset;
        private int _freeSize;

        private readonly VulkanRenderer _gd;
        private readonly BufferHolder _buffer;

        private struct PendingCopy
        {
            public FenceHolder Fence { get; }
            public int Size { get; }

            public PendingCopy(FenceHolder fence, int size)
            {
                Fence = fence;
                Size = size;
                fence.Get();
            }
        }

        private readonly Queue<PendingCopy> _pendingCopies;

        public StagingBuffer(VulkanRenderer gd, BufferManager bufferManager)
        {
            _gd = gd;
            _buffer = bufferManager.Create(gd, BufferSize);
            _pendingCopies = new Queue<PendingCopy>();
            _freeSize = BufferSize;
        }

        public unsafe void PushData(CommandBufferPool cbp, CommandBufferScoped? cbs, Action endRenderPass, BufferHolder dst, int dstOffset, ReadOnlySpan<byte> data)
        {
            bool isRender = cbs != null;
            CommandBufferScoped scoped = cbs ?? cbp.Rent();

            // Must push all data to the buffer. If it can't fit, split it up.

            endRenderPass?.Invoke();

            while (data.Length > 0)
            {
                if (_freeSize < data.Length)
                {
                    FreeCompleted();
                }

                while (_freeSize == 0)
                {
                    if (!WaitFreeCompleted(cbp))
                    {
                        if (isRender)
                        {
                            _gd.FlushAllCommands();
                            scoped = cbp.Rent();
                            isRender = false;
                        }
                        else
                        {
                            scoped = cbp.ReturnAndRent(scoped);
                        }
                    }
                }

                int chunkSize = Math.Min(_freeSize, data.Length);

                PushDataImpl(scoped, dst, dstOffset, data.Slice(0, chunkSize));

                dstOffset += chunkSize;
                data = data.Slice(chunkSize);
            }

            if (!isRender)
            {
                scoped.Dispose();
            }
        }

        private void PushDataImpl(CommandBufferScoped cbs, BufferHolder dst, int dstOffset, ReadOnlySpan<byte> data)
        {
            var srcBuffer = _buffer.GetBuffer();
            var dstBuffer = dst.GetBuffer();

            int offset = _freeOffset;
            int capacity = BufferSize - offset;
            if (capacity < data.Length)
            {
                _buffer.SetDataUnchecked(offset, data.Slice(0, capacity));
                _buffer.SetDataUnchecked(0, data.Slice(capacity));

                BufferHolder.Copy(_gd, cbs, srcBuffer, dstBuffer, offset, dstOffset, capacity);
                BufferHolder.Copy(_gd, cbs, srcBuffer, dstBuffer, 0, dstOffset + capacity, data.Length - capacity);
            }
            else
            {
                _buffer.SetDataUnchecked(offset, data);

                BufferHolder.Copy(_gd, cbs, srcBuffer, dstBuffer, offset, dstOffset, data.Length);
            }

            _freeOffset = (offset + data.Length) & (BufferSize - 1);
            _freeSize -= data.Length;
            Debug.Assert(_freeSize >= 0);

            _pendingCopies.Enqueue(new PendingCopy(cbs.GetFence(), data.Length));
        }

        public unsafe bool TryPushData(CommandBufferScoped cbs, Action endRenderPass, BufferHolder dst, int dstOffset, ReadOnlySpan<byte> data)
        {
            if (data.Length > BufferSize)
            {
                return false;
            }

            if (_freeSize < data.Length)
            {
                FreeCompleted();

                if (_freeSize < data.Length)
                {
                    return false;
                }
            }

            endRenderPass?.Invoke();

            PushDataImpl(cbs, dst, dstOffset, data);

            return true;
        }

        private StagingBufferReserved ReserveDataImpl(CommandBufferScoped cbs, ReadOnlySpan<byte> data, int alignment)
        {
            // Assumes the caller has already determined that there is enough space.
            int offset = BitUtils.AlignUp(_freeOffset, alignment);
            int padding = offset - _freeOffset;

            int capacity = Math.Min(_freeSize, BufferSize - offset);
            int reservedLength = data.Length + padding;
            if (capacity < data.Length)
            {
                offset = 0; // Place at start.
                reservedLength += capacity;
            }

            _buffer.SetDataUnchecked(offset, data);

            _freeOffset = (_freeOffset + reservedLength) & (BufferSize - 1);
            _freeSize -= reservedLength;
            Debug.Assert(_freeSize >= 0);

            _pendingCopies.Enqueue(new PendingCopy(cbs.GetFence(), reservedLength));

            return new StagingBufferReserved(_buffer, offset, data.Length);
        }

        private int GetContiguousFreeSize(int alignment)
        {
            int alignedFreeOffset = BitUtils.AlignUp(_freeOffset, alignment);
            int padding = alignedFreeOffset - _freeOffset;

            // Free regions:
            // - Aligned free offset to end (minimum free size - padding)
            // - 0 to _freeOffset + freeSize wrapped (only if free area contains 0)

            int endOffset = (_freeOffset + _freeSize) & (BufferSize - 1);

            return Math.Max(
                Math.Min(_freeSize - padding, BufferSize - alignedFreeOffset), 
                endOffset < _freeOffset ? Math.Min(_freeSize, endOffset) : 0
                );
        }

        /// <summary>
        /// Reserve a range on the staging buffer for the current command buffer and upload data to it.
        /// </summary>
        /// <param name="cbs">Command buffer to reserve the data on</param>
        /// <param name="data">The data to upload</param>
        /// <param name="alignment">The required alignment for the buffer offset</param>
        /// <returns>The reserved range of the staging buffer</returns>
        public unsafe StagingBufferReserved? TryReserveData(CommandBufferScoped cbs, ReadOnlySpan<byte> data, int alignment = 256)
        {
            if (data.Length > BufferSize)
            {
                return null;
            }

            // Temporary reserved data cannot be fragmented.

            if (GetContiguousFreeSize(alignment) < data.Length)
            {
                FreeCompleted();

                if (GetContiguousFreeSize(alignment) < data.Length)
                {
                    return null;
                }
            }

            return ReserveDataImpl(cbs, data, alignment);
        }

        private bool WaitFreeCompleted(CommandBufferPool cbp)
        {
            if (_pendingCopies.TryPeek(out var pc))
            {
                if (!pc.Fence.IsSignaled())
                {
                    if (cbp.IsFenceOnRentedCommandBuffer(pc.Fence))
                    {
                        return false;
                    }

                    pc.Fence.Wait();
                }

                var dequeued = _pendingCopies.Dequeue();
                Debug.Assert(dequeued.Fence == pc.Fence);
                _freeSize += pc.Size;
                pc.Fence.Put();
            }

            return true;
        }

        private void FreeCompleted()
        {
            FenceHolder signalledFence = null;
            while (_pendingCopies.TryPeek(out var pc) && (pc.Fence == signalledFence || pc.Fence.IsSignaled()))
            {
                signalledFence = pc.Fence; // Already checked - don't need to do it again.
                var dequeued = _pendingCopies.Dequeue();
                Debug.Assert(dequeued.Fence == pc.Fence);
                _freeSize += pc.Size;
                pc.Fence.Put();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _buffer.Dispose();

                while (_pendingCopies.TryDequeue(out var pc))
                {
                    pc.Fence.Put();
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
    }
}
