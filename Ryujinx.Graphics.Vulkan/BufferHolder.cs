using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Ryujinx.Graphics.Vulkan
{
    class BufferHolder : IDisposable, IMirrorable<DisposableBuffer>
    {
        private const int MaxUpdateBufferSize = 0x10000;

        public const AccessFlags DefaultAccessFlags =
            AccessFlags.AccessIndirectCommandReadBit |
            AccessFlags.AccessShaderReadBit |
            AccessFlags.AccessShaderWriteBit |
            AccessFlags.AccessTransferReadBit |
            AccessFlags.AccessTransferWriteBit |
            AccessFlags.AccessUniformReadBit;

        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private readonly MemoryAllocation _allocation;
        private readonly Auto<DisposableBuffer> _buffer;
        private readonly Auto<MemoryAllocation> _allocationAuto;
        private readonly ulong _bufferHandle;

        private CacheByRange<BufferHolder> _cachedConvertedBuffers;

        public int Size { get; }

        private IntPtr _map;

        private readonly MultiFenceHolder _waitable;

        private bool _lastAccessIsWrite;

        private byte[] _pendingData;
        private BufferRangeList _pendingDataRanges;
        private Dictionary<Tuple<int, int>, StagingBufferReserved> _mirrors;


        public BufferHolder(VulkanRenderer gd, Device device, VkBuffer buffer, MemoryAllocation allocation, int size)
        {
            _gd = gd;
            _device = device;
            _allocation = allocation;
            _allocationAuto = new Auto<MemoryAllocation>(allocation);
            _waitable = new MultiFenceHolder(size);
            _buffer = new Auto<DisposableBuffer>(new DisposableBuffer(gd.Api, device, buffer), this, _waitable, _allocationAuto);
            _bufferHandle = buffer.Handle;
            Size = size;
            _map = allocation.HostPointer;
        }

        public unsafe Auto<DisposableBufferView> CreateView(VkFormat format, int offset, int size)
        {
            var bufferViewCreateInfo = new BufferViewCreateInfo()
            {
                SType = StructureType.BufferViewCreateInfo,
                Buffer = new VkBuffer(_bufferHandle),
                Format = format,
                Offset = (uint)offset,
                Range = (uint)size
            };

            _gd.Api.CreateBufferView(_device, bufferViewCreateInfo, null, out var bufferView).ThrowOnError();

            return new Auto<DisposableBufferView>(new DisposableBufferView(_gd.Api, _device, bufferView), _waitable, _buffer);
        }

        public unsafe void InsertBarrier(CommandBuffer commandBuffer, bool isWrite)
        {
            // If the last access is write, we always need a barrier to be sure we will read or modify
            // the correct data.
            // If the last access is read, and current one is a write, we need to wait until the
            // read finishes to avoid overwriting data still in use.
            // Otherwise, if the last access is a read and the current one too, we don't need barriers.
            bool needsBarrier = isWrite || _lastAccessIsWrite;

            _lastAccessIsWrite = isWrite;

            if (needsBarrier)
            {
                MemoryBarrier memoryBarrier = new MemoryBarrier()
                {
                    SType = StructureType.MemoryBarrier,
                    SrcAccessMask = DefaultAccessFlags,
                    DstAccessMask = DefaultAccessFlags
                };

                _gd.Api.CmdPipelineBarrier(
                    commandBuffer,
                    PipelineStageFlags.PipelineStageAllCommandsBit,
                    PipelineStageFlags.PipelineStageAllCommandsBit,
                    DependencyFlags.DependencyDeviceGroupBit,
                    1,
                    memoryBarrier,
                    0,
                    null,
                    0,
                    null);
            }
        }

        private unsafe bool TryGetMirror(CommandBufferScoped cbs, ref int offset, int size, out Auto<DisposableBuffer> buffer)
        {
            // Does this binding need to be mirrored?

            if (!_pendingDataRanges.OverlapsWith(0, offset, size))
            {
                buffer = null;
                return false;
            }

            var key = new Tuple<int, int>(offset, size);

            if (_mirrors.TryGetValue(key, out StagingBufferReserved reserved))
            {
                buffer = reserved.Buffer.GetBuffer();
                offset = reserved.Offset;

                return true;
            }

            // Build data for the new mirror.

            var baseData = new Span<byte>((void*)(_map + offset), size);
            var modData = _pendingData.AsSpan(offset, size);

            Span<byte> mirrorData = _pendingDataRanges.FillData(0, baseData, modData, offset);

            StagingBufferReserved? newMirror = _gd.BufferManager.StagingBuffer.TryReserveData(cbs, mirrorData);

            if (newMirror != null)
            {
                if (_mirrors.Count == 0)
                {
                    _gd.PipelineInternal.RegisterActiveMirror(this);
                }

                _mirrors.Add(key, newMirror.Value);

                buffer = newMirror.Value.Buffer.GetBuffer();
                offset = newMirror.Value.Offset;

                return true;
            }
            else
            {
                // Data could not be placed on the mirror, likely out of space. Force the data to flush.
                ClearMirrors(cbs, offset, size);

                buffer = null;
                return false;
            }
        }

        public Auto<DisposableBuffer> GetBuffer()
        {
            return _buffer;
        }

        public Auto<DisposableBuffer> GetBuffer(CommandBuffer commandBuffer, bool isWrite = false)
        {
            if (isWrite)
            {
                SignalWrite(0, Size);
            }

            return _buffer;
        }

        public Auto<DisposableBuffer> GetBuffer(CommandBuffer commandBuffer, int offset, int size, bool isWrite = false)
        {
            if (isWrite)
            {
                SignalWrite(offset, size);
            }

            return _buffer;
        }

        public Auto<DisposableBuffer> GetMirrorable(CommandBufferScoped cbs, ref int offset, int size)
        {
            if (_pendingData != null && TryGetMirror(cbs, ref offset, size, out Auto<DisposableBuffer> result))
            {
                return result;
            }

            return _buffer;
        }

        public void ClearMirrors()
        {
            // Clear mirrors without forcing a flush. This happens when the command buffer is switched,
            // as all reserved areas on the staging buffer are released.

            if (_pendingData != null)
            {
                _mirrors.Clear();
            };
        }

        public void ClearMirrors(CommandBufferScoped cbs, int offset, int size)
        {
            // Clear mirrors in the given range, and submit overlapping pending data.

            // TODO: within range. for now it just clears everything.

            if (_pendingData != null)
            {
                bool hadMirrors = _mirrors.Count > 0;
                _mirrors.Clear();

                if (_pendingDataRanges.Count(0) != 0)
                {
                    UploadPendingData(cbs, offset, size);
                }

                if (hadMirrors)
                {
                    _gd.PipelineInternal.Rebind(_buffer, 0, Size);
                }
            };
        }

        private void UploadPendingData(CommandBufferScoped cbs, int offset, int size)
        {
            var ranges = _pendingDataRanges.All(0);

            // TODO: within range. for now it just clears everything.

            if (ranges != null)
            {
                var rangeCopy = ranges.ToArray();

                _pendingDataRanges.Clear(0);

                foreach (var range in rangeCopy)
                {
                    if (_gd.PipelineInternal.CurrentCommandBuffer.CommandBuffer.Handle == cbs.CommandBuffer.Handle)
                    {
                        SetData(range.Offset, _pendingData.AsSpan(range.Offset, range.Size), cbs, _gd.PipelineInternal.EndRenderPass, false);
                    }
                    else
                    {
                        SetData(range.Offset, _pendingData.AsSpan(range.Offset, range.Size), cbs, null, false);
                    }
                }
            }
        }

        public void SignalWrite(int offset, int size)
        {
            if (offset == 0 && size == Size)
            {
                _cachedConvertedBuffers.Clear();
            }
            else
            {
                _cachedConvertedBuffers.ClearRange(offset, size);
            }
        }

        public BufferHandle GetHandle()
        {
            var handle = _bufferHandle;
            return Unsafe.As<ulong, BufferHandle>(ref handle);
        }

        public unsafe IntPtr Map(int offset, int mappingSize)
        {
            return _map;
        }

        public unsafe ReadOnlySpan<byte> GetData(int offset, int size)
        {
            if (_map != IntPtr.Zero)
            {
                return GetDataStorage(offset, size);
            }
            else
            {
                BackgroundResource resource = _gd.BackgroundResources.Get();

                if (_gd.CommandBufferPool.OwnedByCurrentThread)
                {
                    _gd.FlushAllCommands();

                    return resource.GetFlushBuffer().GetBufferData(_gd.CommandBufferPool, this, offset, size);
                }
                else
                {
                    return resource.GetFlushBuffer().GetBufferData(resource.GetPool(), this, offset, size);
                }
            }
        }

        public unsafe Span<byte> GetDataStorage(int offset, int size)
        {
            int mappingSize = Math.Min(size, Size - offset);

            if (_map != IntPtr.Zero)
            {
                return new Span<byte>((void*)(_map + offset), mappingSize);
            }

            throw new InvalidOperationException("The buffer is not host mapped.");
        }

        public bool RemoveOverlappingMirrors(int offset, int size)
        {
            List<Tuple<int, int>> toRemove = null;
            foreach (var key in _mirrors.Keys)
            {
                if (!(offset + size <= key.Item1 || offset >= key.Item1 + key.Item2))
                {
                    if (toRemove == null)
                    {
                        toRemove = new List<Tuple<int, int>>();
                    }

                    toRemove.Add(key);
                }
            }

            if (toRemove != null)
            {
                foreach (var key in toRemove)
                {
                    _mirrors.Remove(key);
                }

                return true;
            }

            return false;
        }

        public unsafe void SetData(int offset, ReadOnlySpan<byte> data, CommandBufferScoped? cbs = null, Action endRenderPass = null, bool allowCbsWait = true)
        {
            int dataSize = Math.Min(data.Length, Size - offset);
            if (dataSize == 0)
            {
                return;
            }

            bool allowMirror = allowCbsWait && cbs != null;

            if (_map != IntPtr.Zero)
            {
                // If persistently mapped, set the data directly if the buffer is not currently in use.
                bool isRented = _buffer.HasRentedCommandBufferDependency(_gd.CommandBufferPool);

                // If the buffer is rented, take a little more time and check if the use overlaps this handle.
                bool needsFlush = isRented && _waitable.IsBufferRangeInUse(offset, dataSize, false);

                if (!needsFlush)
                {
                    WaitForFences(offset, dataSize);

                    data.Slice(0, dataSize).CopyTo(new Span<byte>((void*)(_map + offset), dataSize));

                    if (_pendingData != null)
                    {
                        bool removed = _pendingDataRanges.Remove(0, offset, dataSize);
                        if (RemoveOverlappingMirrors(offset, dataSize) || removed)
                        {
                            // If any mirrors were removed, rebind the buffer range.
                            _gd.PipelineInternal.Rebind(_buffer, offset, dataSize);
                        }
                    }

                    SignalWrite(offset, dataSize);

                    return;
                }
            }

            // If the buffer does not have an in-flight write (including an inline update), then upload data to a pendingCopy.
            if (allowMirror && !_waitable.IsBufferRangeInUse(offset, dataSize, true))
            {
                if (_pendingData == null)
                {
                    _pendingData = new byte[Size];
                    _pendingDataRanges.Initialize();
                    _mirrors = new Dictionary<Tuple<int, int>, StagingBufferReserved>();
                }

                data.Slice(0, dataSize).CopyTo(_pendingData.AsSpan(offset, dataSize));
                _pendingDataRanges.Add(0, offset, dataSize);

                // Remove any overlapping mirrors.
                RemoveOverlappingMirrors(offset, dataSize);

                // Tell the graphics device to rebind any constant buffer that overlaps the newly modified range, as it should access a mirror.
                _gd.PipelineInternal.Rebind(_buffer, offset, dataSize);

                return;
            }

            if (_pendingData != null)
            {
                _pendingDataRanges.Remove(0, offset, dataSize);
            }

            if (cbs != null && !(_buffer.HasCommandBufferDependency(cbs.Value) && _waitable.IsBufferRangeInUse(cbs.Value.CommandBufferIndex, offset, dataSize)))
            {
                // If the buffer hasn't been used on the command buffer yet, try to preload the data.
                // This avoids ending and beginning render passes on each buffer data upload.

                cbs = _gd.PipelineInternal.GetPreloadCommandBuffer();
                endRenderPass = null;
            }

            if (cbs == null ||
                !VulkanConfiguration.UseFastBufferUpdates ||
                data.Length > MaxUpdateBufferSize ||
                !TryPushData(cbs.Value, endRenderPass, offset, data))
            {
                if (allowCbsWait)
                {
                    _gd.BufferManager.StagingBuffer.PushData(_gd.CommandBufferPool, cbs, endRenderPass, this, offset, data);
                }
                else
                {
                    bool rentCbs = cbs == null;
                    if (rentCbs)
                    {
                        cbs = _gd.CommandBufferPool.Rent();
                    }

                    if (!_gd.BufferManager.StagingBuffer.TryPushData(cbs.Value, endRenderPass, this, offset, data))
                    {
                        // Need to do a slow upload.
                        BufferHolder srcHolder = _gd.BufferManager.Create(_gd, dataSize, deviceLocal: false);
                        srcHolder.SetDataUnchecked(offset, data);

                        var srcBuffer = srcHolder.GetBuffer();
                        var dstBuffer = this.GetBuffer(cbs.Value.CommandBuffer, true);

                        Copy(_gd, cbs.Value, srcBuffer, dstBuffer, 0, offset, dataSize);

                        srcHolder.Dispose();
                    }

                    if (rentCbs)
                    {
                        cbs.Value.Dispose();
                    }
                }
            }
        }

        public unsafe void SetDataUnchecked(int offset, ReadOnlySpan<byte> data)
        {
            int dataSize = Math.Min(data.Length, Size - offset);
            if (dataSize == 0)
            {
                return;
            }

            if (_map != IntPtr.Zero)
            {
                data.Slice(0, dataSize).CopyTo(new Span<byte>((void*)(_map + offset), dataSize));
            }
            else
            {
                _gd.BufferManager.StagingBuffer.PushData(_gd.CommandBufferPool, null, null, this, offset, data);
            }
        }

        public void SetDataInline(CommandBufferScoped cbs, Action endRenderPass, int dstOffset, ReadOnlySpan<byte> data)
        {
            if (!TryPushData(cbs, endRenderPass, dstOffset, data))
            {
                throw new ArgumentException($"Invalid offset 0x{dstOffset:X} or data size 0x{data.Length:X}.");
            }
        }

        private unsafe bool TryPushData(CommandBufferScoped cbs, Action endRenderPass, int dstOffset, ReadOnlySpan<byte> data)
        {
            if ((dstOffset & 3) != 0 || (data.Length & 3) != 0)
            {
                return false;
            }

            endRenderPass?.Invoke();

            var dstBuffer = GetBuffer(cbs.CommandBuffer, dstOffset, data.Length, true).Get(cbs, dstOffset, data.Length, true).Value;

            InsertBufferBarrier(
                _gd,
                cbs.CommandBuffer,
                dstBuffer,
                BufferHolder.DefaultAccessFlags,
                AccessFlags.AccessTransferWriteBit,
                PipelineStageFlags.PipelineStageAllCommandsBit,
                PipelineStageFlags.PipelineStageTransferBit,
                dstOffset,
                data.Length);

            fixed (byte* pData = data)
            {
                for (ulong offset = 0; offset < (ulong)data.Length;)
                {
                    ulong size = Math.Min(MaxUpdateBufferSize, (ulong)data.Length - offset);
                    _gd.Api.CmdUpdateBuffer(cbs.CommandBuffer, dstBuffer, (ulong)dstOffset + offset, size, pData + offset);
                    offset += size;
                }
            }

            InsertBufferBarrier(
                _gd,
                cbs.CommandBuffer,
                dstBuffer,
                AccessFlags.AccessTransferWriteBit,
                BufferHolder.DefaultAccessFlags,
                PipelineStageFlags.PipelineStageTransferBit,
                PipelineStageFlags.PipelineStageAllCommandsBit,
                dstOffset,
                data.Length);

            return true;
        }

        public static unsafe void Copy(
            VulkanRenderer gd,
            CommandBufferScoped cbs,
            Auto<DisposableBuffer> src,
            Auto<DisposableBuffer> dst,
            int srcOffset,
            int dstOffset,
            int size)
        {
            var srcBuffer = src.Get(cbs, srcOffset, size).Value;
            var dstBuffer = dst.Get(cbs, dstOffset, size, true).Value;

            InsertBufferBarrier(
                gd,
                cbs.CommandBuffer,
                dstBuffer,
                BufferHolder.DefaultAccessFlags,
                AccessFlags.AccessTransferWriteBit,
                PipelineStageFlags.PipelineStageAllCommandsBit,
                PipelineStageFlags.PipelineStageTransferBit,
                dstOffset,
                size);

            var region = new BufferCopy((ulong)srcOffset, (ulong)dstOffset, (ulong)size);

            gd.Api.CmdCopyBuffer(cbs.CommandBuffer, srcBuffer, dstBuffer, 1, &region);

            InsertBufferBarrier(
                gd,
                cbs.CommandBuffer,
                dstBuffer,
                AccessFlags.AccessTransferWriteBit,
                BufferHolder.DefaultAccessFlags,
                PipelineStageFlags.PipelineStageTransferBit,
                PipelineStageFlags.PipelineStageAllCommandsBit,
                dstOffset,
                size);
        }

        public static unsafe void InsertBufferBarrier(
            VulkanRenderer gd,
            CommandBuffer commandBuffer,
            VkBuffer buffer,
            AccessFlags srcAccessMask,
            AccessFlags dstAccessMask,
            PipelineStageFlags srcStageMask,
            PipelineStageFlags dstStageMask,
            int offset,
            int size)
        {
            BufferMemoryBarrier memoryBarrier = new BufferMemoryBarrier()
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = srcAccessMask,
                DstAccessMask = dstAccessMask,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = buffer,
                Offset = (ulong)offset,
                Size = (ulong)size
            };

            gd.Api.CmdPipelineBarrier(
                commandBuffer,
                srcStageMask,
                dstStageMask,
                0,
                0,
                null,
                1,
                memoryBarrier,
                0,
                null);
        }

        public void WaitForFences()
        {
            _waitable.WaitForFences(_gd.Api, _device);
        }

        public void WaitForFences(int offset, int size)
        {
            _waitable.WaitForFences(_gd.Api, _device, offset, size);
        }

        private bool BoundToRange(int offset, ref int size)
        {
            if (offset >= Size)
            {
                return false;
            }

            size = Math.Min(Size - offset, size);

            return true;
        }

        public Auto<DisposableBuffer> GetBufferI8ToI16(CommandBufferScoped cbs, int offset, int size)
        {
            if (!BoundToRange(offset, ref size))
            {
                return null;
            }

            var key = new I8ToI16CacheKey(_gd);

            if (!_cachedConvertedBuffers.TryGetValue(offset, size, key, out var holder))
            {
                holder = _gd.BufferManager.Create(_gd, (size * 2 + 3) & ~3);

                _gd.PipelineInternal.EndRenderPass();
                _gd.HelperShader.ConvertI8ToI16(_gd, cbs, this, holder, offset, size);

                key.SetBuffer(holder.GetBuffer());

                _cachedConvertedBuffers.Add(offset, size, key, holder);
            }

            return holder.GetBuffer();
        }

        public Auto<DisposableBuffer> GetAlignedVertexBuffer(CommandBufferScoped cbs, int offset, int size, int stride, int alignment)
        {
            if (!BoundToRange(offset, ref size))
            {
                return null;
            }

            var key = new AlignedVertexBufferCacheKey(_gd, stride, alignment);

            if (!_cachedConvertedBuffers.TryGetValue(offset, size, key, out var holder))
            {
                int alignedStride = (stride + (alignment - 1)) & -alignment;

                holder = _gd.BufferManager.Create(_gd, (size / stride) * alignedStride);

                _gd.PipelineInternal.EndRenderPass();
                _gd.HelperShader.ChangeStride(_gd, cbs, this, holder, offset, size, stride, alignedStride);

                key.SetBuffer(holder.GetBuffer());

                _cachedConvertedBuffers.Add(offset, size, key, holder);
            }

            return holder.GetBuffer();
        }

        public Auto<DisposableBuffer> GetBufferTopologyConversion(CommandBufferScoped cbs, int offset, int size, IndexBufferPattern pattern, int indexSize)
        {
            if (!BoundToRange(offset, ref size))
            {
                return null;
            }

            var key = new TopologyConversionCacheKey(_gd, pattern, indexSize);

            if (!_cachedConvertedBuffers.TryGetValue(offset, size, key, out var holder))
            {
                // The destination index size is always I32.

                int indexCount = size / indexSize;

                int convertedCount = pattern.GetConvertedCount(indexCount);

                holder = _gd.BufferManager.Create(_gd, convertedCount * 4);

                _gd.PipelineInternal.EndRenderPass();
                _gd.HelperShader.ConvertIndexBuffer(_gd, cbs, this, holder, pattern, indexSize, offset, indexCount);

                key.SetBuffer(holder.GetBuffer());

                _cachedConvertedBuffers.Add(offset, size, key, holder);
            }

            return holder.GetBuffer();
        }

        public void Dispose()
        {
            _gd.PipelineInternal?.FlushCommandsIfWeightExceeding(_buffer, (ulong)Size);

            _buffer.Dispose();
            _allocationAuto.Dispose();
            _cachedConvertedBuffers.Dispose();
        }
    }
}
