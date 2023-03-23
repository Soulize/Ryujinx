﻿using Ryujinx.Common;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Gpu.Memory
{
    /// <summary>
    /// Buffer manager.
    /// </summary>
    class BufferManager
    {
        private readonly GpuContext _context;
        private readonly GpuChannel _channel;

        private int _unalignedStorageBuffers;
        public bool HasUnalignedStorageBuffers => _unalignedStorageBuffers > 0;

        private IndexBuffer _indexBuffer;
        private readonly VertexBuffer[] _vertexBuffers;
        private readonly BufferBounds[] _transformFeedbackBuffers;
        private readonly List<BufferTextureBinding> _bufferTextures;
        private readonly BufferAssignment[] _ranges;

        /// <summary>
        /// Holds shader stage buffer state and binding information.
        /// </summary>
        private class BuffersPerStage
        {
            /// <summary>
            /// Shader buffer binding information.
            /// </summary>
            public BufferDescriptor[] Bindings { get; private set; }

            /// <summary>
            /// Buffer regions.
            /// </summary>
            public BufferBounds[] Buffers { get; }

            /// <summary>
            /// Flag indicating if this binding is unaligned.
            /// </summary>
            public bool[] Unaligned { get; }

            /// <summary>
            /// Total amount of buffers used on the shader.
            /// </summary>
            public int Count { get; private set; }

            /// <summary>
            /// Creates a new instance of the shader stage buffer information.
            /// </summary>
            /// <param name="count">Maximum amount of buffers that the shader stage can use</param>
            public BuffersPerStage(int count)
            {
                Bindings = new BufferDescriptor[count];
                Buffers = new BufferBounds[count];
                Unaligned = new bool[count];
            }

            /// <summary>
            /// Sets the region of a buffer at a given slot.
            /// </summary>
            /// <param name="index">Buffer slot</param>
            /// <param name="gpuVa">Region virtual address</param>
            /// <param name="size">Region size in bytes</param>
            /// <param name="flags">Buffer usage flags</param>
            /// <return>True if the binding has changed, false otherwise</return>
            public bool SetBounds(MemoryManager memoryManager, int index, ulong gpuVa, ulong size, BufferUsageFlags flags = BufferUsageFlags.None)
            {
                return BufferBounds.TranslateAndCreateBuffer(memoryManager, ref Buffers[index], gpuVa, size, flags);
            }

            /// <summary>
            /// Sets shader buffer binding information.
            /// </summary>
            /// <param name="descriptors">Buffer binding information</param>
            public void SetBindings(BufferDescriptor[] descriptors)
            {
                if (descriptors == null)
                {
                    Count = 0;
                    return;
                }

                if ((Count = descriptors.Length) != 0)
                {
                    Bindings = descriptors;
                }
            }
        }

        private readonly BuffersPerStage _cpStorageBuffers;
        private readonly BuffersPerStage _cpUniformBuffers;
        private readonly BuffersPerStage[] _gpStorageBuffers;
        private readonly BuffersPerStage[] _gpUniformBuffers;
        private readonly BufferRange[] _gpStorageBindings;
        private readonly BufferRange[] _gpUniformBindings;

        private bool _gpStorageBuffersDirty;
        private bool _gpUniformBuffersDirty;

        private bool _indexBufferDirty;
        private bool _vertexBuffersDirty;
        private uint _vertexBuffersDirtyMask;
        private uint _vertexBuffersEnableMask;
        private bool _transformFeedbackBuffersDirty;

        private bool _rebind;

        /// <summary>
        /// Creates a new instance of the buffer manager.
        /// </summary>
        /// <param name="context">GPU context that the buffer manager belongs to</param>
        /// <param name="channel">GPU channel that the buffer manager belongs to</param>
        public BufferManager(GpuContext context, GpuChannel channel)
        {
            _context = context;
            _channel = channel;

            _vertexBuffers = new VertexBuffer[Constants.TotalVertexBuffers];

            _transformFeedbackBuffers = new BufferBounds[Constants.TotalTransformFeedbackBuffers];

            _cpStorageBuffers = new BuffersPerStage(Constants.TotalCpStorageBuffers);
            _cpUniformBuffers = new BuffersPerStage(Constants.TotalCpUniformBuffers);

            _gpStorageBuffers = new BuffersPerStage[Constants.ShaderStages];
            _gpUniformBuffers = new BuffersPerStage[Constants.ShaderStages];

            _gpStorageBindings = new BufferRange[Constants.ShaderStages * Constants.TotalGpStorageBuffers];
            _gpUniformBindings = new BufferRange[Constants.ShaderStages * Constants.TotalGpUniformBuffers];

            for (int index = 0; index < Constants.ShaderStages; index++)
            {
                _gpStorageBuffers[index] = new BuffersPerStage(Constants.TotalGpStorageBuffers);
                _gpUniformBuffers[index] = new BuffersPerStage(Constants.TotalGpUniformBuffers);
            }

            _bufferTextures = new List<BufferTextureBinding>();

            _ranges = new BufferAssignment[Constants.TotalGpUniformBuffers * Constants.ShaderStages];
        }


        /// <summary>
        /// Sets the memory range with the index buffer data, to be used for subsequent draw calls.
        /// </summary>
        /// <param name="gpuVa">Start GPU virtual address of the index buffer</param>
        /// <param name="size">Size, in bytes, of the index buffer</param>
        /// <param name="type">Type of each index buffer element</param>
        public void SetIndexBuffer(ulong gpuVa, ulong size, IndexType type)
        {
            ref IndexBuffer ib = ref _indexBuffer;

            if (ib.GpuAddress != gpuVa || ib.Size != size)
            {
                (ulong address, Buffer buffer) = _channel.MemoryManager.Physical.BufferCache.TranslateAndCreateBuffer(_channel.MemoryManager, gpuVa, size);

                ib.GpuAddress = gpuVa;
                ib.Address = address;
                ib.Size = size;

                _indexBufferDirty = true;
            }

            if (ib.Type != type)
            {
                ib.Type = type;

                _indexBufferDirty = true;
            }
        }

        /// <summary>
        /// Sets a new index buffer that overrides the one set on the call to <see cref="CommitGraphicsBindings"/>.
        /// </summary>
        /// <param name="buffer">Buffer to be used as index buffer</param>
        /// <param name="type">Type of each index buffer element</param>
        public void SetIndexBuffer(BufferRange buffer, IndexType type)
        {
            _context.Renderer.Pipeline.SetIndexBuffer(buffer, type);

            _indexBufferDirty = true;
        }

        /// <summary>
        /// Sets the memory range with vertex buffer data, to be used for subsequent draw calls.
        /// </summary>
        /// <param name="index">Index of the vertex buffer (up to 16)</param>
        /// <param name="gpuVa">GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the buffer</param>
        /// <param name="stride">Stride of the buffer, defined as the number of bytes of each vertex</param>
        /// <param name="divisor">Vertex divisor of the buffer, for instanced draws</param>
        public void SetVertexBuffer(int index, ulong gpuVa, ulong size, int stride, int divisor)
        {
            ref VertexBuffer vb = ref _vertexBuffers[index];

            uint bit = 1u << index;
            bool dirty = false;

            if (vb.GpuAddress != gpuVa || vb.Size != size)
            {
                (ulong address, Buffer buffer) = _channel.MemoryManager.Physical.BufferCache.TranslateAndCreateBuffer(_channel.MemoryManager, gpuVa, size);

                vb.GpuAddress = gpuVa;
                vb.Address = address;
                vb.Size = size;

                if (address != 0)
                {
                    _vertexBuffersEnableMask |= bit;
                }
                else
                {
                    _vertexBuffersEnableMask &= ~(bit);
                }

                dirty = true;
            }

            if (vb.Stride != stride || vb.Divisor != divisor)
            {
                vb.Stride = stride;
                vb.Divisor = divisor;

                dirty = true;
            }

            if (dirty)
            {
                _vertexBuffersDirtyMask |= bit & _vertexBuffersEnableMask;
            }
        }

        /// <summary>
        /// Sets a transform feedback buffer on the graphics pipeline.
        /// The output from the vertex transformation stages are written into the feedback buffer.
        /// </summary>
        /// <param name="index">Index of the transform feedback buffer</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the transform feedback buffer</param>
        public void SetTransformFeedbackBuffer(int index, ulong gpuVa, ulong size)
        {
            if (BufferBounds.TranslateAndCreateBuffer(_channel.MemoryManager, ref _transformFeedbackBuffers[index], gpuVa, size))
            {
                _transformFeedbackBuffersDirty = true;
            }
        }

        /// <summary>
        /// Records the alignment of a storage buffer.
        /// Unaligned storage buffers disable some optimizations on the shader.
        /// </summary>
        /// <param name="buffers">The binding list to modify</param>
        /// <param name="index">Index of the storage buffer</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        private void RecordStorageAlignment(BuffersPerStage buffers, int index, ulong gpuVa)
        {
            bool unaligned = (gpuVa & (Constants.StorageAlignment - 1)) != 0;

            if (unaligned || HasUnalignedStorageBuffers)
            {
                // Check if the alignment changed for this binding.

                ref bool currentUnaligned = ref buffers.Unaligned[index];

                if (currentUnaligned != unaligned)
                {
                    currentUnaligned = unaligned;
                    _unalignedStorageBuffers += unaligned ? 1 : -1;
                }
            }
        }

        /// <summary>
        /// Sets a storage buffer on the compute pipeline.
        /// Storage buffers can be read and written to on shaders.
        /// </summary>
        /// <param name="index">Index of the storage buffer</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the storage buffer</param>
        /// <param name="flags">Buffer usage flags</param>
        public void SetComputeStorageBuffer(int index, ulong gpuVa, ulong size, BufferUsageFlags flags)
        {
            size += gpuVa & ((ulong)_context.Capabilities.StorageBufferOffsetAlignment - 1);

            RecordStorageAlignment(_cpStorageBuffers, index, gpuVa);

            gpuVa = BitUtils.AlignDown<ulong>(gpuVa, (ulong)_context.Capabilities.StorageBufferOffsetAlignment);

            _cpStorageBuffers.SetBounds(_channel.MemoryManager, index, gpuVa, size, flags);
        }

        /// <summary>
        /// Sets a storage buffer on the graphics pipeline.
        /// Storage buffers can be read and written to on shaders.
        /// </summary>
        /// <param name="stage">Index of the shader stage</param>
        /// <param name="index">Index of the storage buffer</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the storage buffer</param>
        /// <param name="flags">Buffer usage flags</param>
        public void SetGraphicsStorageBuffer(int stage, int index, ulong gpuVa, ulong size, BufferUsageFlags flags)
        {
            size += gpuVa & ((ulong)_context.Capabilities.StorageBufferOffsetAlignment - 1);

            BuffersPerStage buffers = _gpStorageBuffers[stage];

            RecordStorageAlignment(buffers, index, gpuVa);

            gpuVa = BitUtils.AlignDown<ulong>(gpuVa, (ulong)_context.Capabilities.StorageBufferOffsetAlignment);

            if (buffers.SetBounds(_channel.MemoryManager, index, gpuVa, size, flags))
            {
                _gpStorageBuffersDirty = true;
            }
        }

        /// <summary>
        /// Sets a uniform buffer on the compute pipeline.
        /// Uniform buffers are read-only from shaders, and have a small capacity.
        /// </summary>
        /// <param name="index">Index of the uniform buffer</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the storage buffer</param>
        public void SetComputeUniformBuffer(int index, ulong gpuVa, ulong size)
        {
            _cpUniformBuffers.SetBounds(_channel.MemoryManager, index, gpuVa, size);
        }

        /// <summary>
        /// Sets a uniform buffer on the graphics pipeline.
        /// Uniform buffers are read-only from shaders, and have a small capacity.
        /// </summary>
        /// <param name="stage">Index of the shader stage</param>
        /// <param name="index">Index of the uniform buffer</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the storage buffer</param>
        public void SetGraphicsUniformBuffer(int stage, int index, ulong gpuVa, ulong size)
        {
            if (_gpUniformBuffers[stage].SetBounds(_channel.MemoryManager, index, gpuVa, size))
            {
                _gpUniformBuffersDirty = true;
            }
        }

        /// <summary>
        /// Sets the binding points for the storage buffers bound on the compute pipeline.
        /// </summary>
        /// <param name="bindings">Bindings for the active shader</param>
        public void SetComputeBufferBindings(CachedShaderBindings bindings)
        {
            _cpStorageBuffers.SetBindings(bindings.StorageBufferBindings[0]);
            _cpUniformBuffers.SetBindings(bindings.ConstantBufferBindings[0]);
        }

        /// <summary>
        /// Sets the binding points for the storage buffers bound on the graphics pipeline.
        /// </summary>
        /// <param name="bindings">Bindings for the active shader</param>
        public void SetGraphicsBufferBindings(CachedShaderBindings bindings)
        {
            for (int i = 0; i < Constants.ShaderStages; i++)
            {
                _gpStorageBuffers[i].SetBindings(bindings.StorageBufferBindings[i]);
                _gpUniformBuffers[i].SetBindings(bindings.ConstantBufferBindings[i]);
            }

            _gpStorageBuffersDirty = true;
            _gpUniformBuffersDirty = true;
        }

        /// <summary>
        /// Gets a bit mask indicating which compute uniform buffers are currently bound.
        /// </summary>
        /// <returns>Mask where each bit set indicates a bound constant buffer</returns>
        public uint GetComputeUniformBufferUseMask()
        {
            uint mask = 0;

            for (int i = 0; i < _cpUniformBuffers.Buffers.Length; i++)
            {
                if (_cpUniformBuffers.Buffers[i].Address != 0)
                {
                    mask |= 1u << i;
                }
            }

            return mask;
        }

        /// <summary>
        /// Gets a bit mask indicating which graphics uniform buffers are currently bound.
        /// </summary>
        /// <param name="stage">Index of the shader stage</param>
        /// <returns>Mask where each bit set indicates a bound constant buffer</returns>
        public uint GetGraphicsUniformBufferUseMask(int stage)
        {
            uint mask = 0;

            for (int i = 0; i < _gpUniformBuffers[stage].Buffers.Length; i++)
            {
                if (_gpUniformBuffers[stage].Buffers[i].Address != 0)
                {
                    mask |= 1u << i;
                }
            }

            return mask;
        }

        /// <summary>
        /// Gets the address of the compute uniform buffer currently bound at the given index.
        /// </summary>
        /// <param name="index">Index of the uniform buffer binding</param>
        /// <returns>The uniform buffer address, or an undefined value if the buffer is not currently bound</returns>
        public ulong GetComputeUniformBufferAddress(int index)
        {
            return _cpUniformBuffers.Buffers[index].Address;
        }

        /// <summary>
        /// Gets the address of the graphics uniform buffer currently bound at the given index.
        /// </summary>
        /// <param name="stage">Index of the shader stage</param>
        /// <param name="index">Index of the uniform buffer binding</param>
        /// <returns>The uniform buffer address, or an undefined value if the buffer is not currently bound</returns>
        public ulong GetGraphicsUniformBufferAddress(int stage, int index)
        {
            return _gpUniformBuffers[stage].Buffers[index].Address;
        }

        /// <summary>
        /// Gets the bounds of the uniform buffer currently bound at the given index.
        /// </summary>
        /// <param name="isCompute">Indicates whenever the uniform is requested by the 3D or compute engine</param>
        /// <param name="stage">Index of the shader stage, if the uniform is for the 3D engine</param>
        /// <param name="index">Index of the uniform buffer binding</param>
        /// <returns>The uniform buffer bounds, or an undefined value if the buffer is not currently bound</returns>
        public ref BufferBounds GetUniformBufferBounds(bool isCompute, int stage, int index)
        {
            if (isCompute)
            {
                return ref _cpUniformBuffers.Buffers[index];
            }
            else
            {
                return ref _gpUniformBuffers[stage].Buffers[index];
            }
        }

        /// <summary>
        /// Ensures that the compute engine bindings are visible to the host GPU.
        /// Note: this actually performs the binding using the host graphics API.
        /// </summary>
        public void CommitComputeBindings()
        {
            var bufferCache = _channel.MemoryManager.Physical.BufferCache;

            BindBuffers(bufferCache, _cpStorageBuffers, isStorage: true);
            BindBuffers(bufferCache, _cpUniformBuffers, isStorage: false);

            CommitBufferTextureBindings();

            // Force rebind after doing compute work.
            Rebind();
        }

        /// <summary>
        /// Commit any queued buffer texture bindings.
        /// </summary>
        private void CommitBufferTextureBindings()
        {
            if (_bufferTextures.Count > 0)
            {
                foreach (var binding in _bufferTextures)
                {
                    var isStore = binding.BindingInfo.Flags.HasFlag(TextureUsageFlags.ImageStore);
                    var range = _channel.MemoryManager.Physical.BufferCache.GetBufferRange(binding.Address, binding.Size, isStore);
                    binding.Texture.SetStorage(range);

                    // The texture must be rebound to use the new storage if it was updated.

                    if (binding.IsImage)
                    {
                        _context.Renderer.Pipeline.SetImage(binding.BindingInfo.Binding, binding.Texture, binding.Format);
                    }
                    else
                    {
                        _context.Renderer.Pipeline.SetTextureAndSampler(binding.Stage, binding.BindingInfo.Binding, binding.Texture, null);
                    }
                }

                _bufferTextures.Clear();
            }
        }

        /// <summary>
        /// Ensures that the graphics engine bindings are visible to the host GPU.
        /// Note: this actually performs the binding using the host graphics API.
        /// </summary>
        public void CommitGraphicsBindings()
        {
            var bufferCache = _channel.MemoryManager.Physical.BufferCache;

            if (_rebind)
            {
                Array.Clear(_gpUniformBindings);
                Array.Clear(_gpStorageBindings);
            }

            if (_indexBufferDirty || _rebind)
            {
                _indexBufferDirty = false;

                if (_indexBuffer.Address != 0)
                {
                    BufferRange buffer = bufferCache.GetBufferRange(_indexBuffer.Address, _indexBuffer.Size);

                    _context.Renderer.Pipeline.SetIndexBuffer(buffer, _indexBuffer.Type);
                }
            }
            else if (_indexBuffer.Address != 0)
            {
                bufferCache.SynchronizeBufferRange(_indexBuffer.Address, _indexBuffer.Size);
            }

            uint vbEnableMask = _vertexBuffersEnableMask;
            uint vbDirtyMask = _rebind ? (1 << Constants.TotalVertexBuffers) - 1 : _vertexBuffersDirtyMask;

            if (vbDirtyMask != 0)
            {
                int start = BitOperations.TrailingZeroCount(vbDirtyMask);
                int end = 32 - BitOperations.LeadingZeroCount(vbDirtyMask);

                _vertexBuffersDirtyMask = 0;

                Span<VertexBufferDescriptor> vertexBuffers = stackalloc VertexBufferDescriptor[Constants.TotalVertexBuffers];

                for (int index = 0; (vbEnableMask >> index) != 0; index++)
                {
                    VertexBuffer vb = _vertexBuffers[index];

                    if (vb.Address == 0)
                    {
                        continue;
                    }

                    BufferRange buffer = bufferCache.GetBufferRange(vb.Address, vb.Size);

                    vertexBuffers[index] = new VertexBufferDescriptor(buffer, vb.Stride, vb.Divisor);
                }

                _context.Renderer.Pipeline.SetVertexBuffers(start, vertexBuffers.Slice(start, end - start));
            }
            else
            {
                for (int index = 0; (vbEnableMask >> index) != 0; index++)
                {
                    VertexBuffer vb = _vertexBuffers[index];

                    if (vb.Address == 0)
                    {
                        continue;
                    }

                    bufferCache.SynchronizeBufferRange(vb.Address, vb.Size);
                }
            }

            if (_transformFeedbackBuffersDirty || _rebind)
            {
                _transformFeedbackBuffersDirty = false;

                Span<BufferRange> tfbs = stackalloc BufferRange[Constants.TotalTransformFeedbackBuffers];

                for (int index = 0; index < Constants.TotalTransformFeedbackBuffers; index++)
                {
                    BufferBounds tfb = _transformFeedbackBuffers[index];

                    if (tfb.Address == 0)
                    {
                        tfbs[index] = BufferRange.Empty;
                        continue;
                    }

                    tfbs[index] = tfb.GetBufferRange(bufferCache);
                }

                _context.Renderer.Pipeline.SetTransformFeedbackBuffers(tfbs);
            }
            else
            {
                for (int index = 0; index < Constants.TotalTransformFeedbackBuffers; index++)
                {
                    BufferBounds tfb = _transformFeedbackBuffers[index];

                    if (tfb.Address == 0)
                    {
                        continue;
                    }

                    tfb.SynchronizeMemory(true);
                }
            }

            if (_gpStorageBuffersDirty || _rebind)
            {
                _gpStorageBuffersDirty = false;

                BindBuffers(bufferCache, _gpStorageBuffers, _gpStorageBindings, isStorage: true);
            }
            else
            {
                UpdateBuffers(_gpStorageBuffers);
            }

            if (_gpUniformBuffersDirty || _rebind)
            {
                _gpUniformBuffersDirty = false;

                BindBuffers(bufferCache, _gpUniformBuffers, _gpUniformBindings, isStorage: false);
            }
            else
            {
                UpdateBuffers(_gpUniformBuffers);
            }

            CommitBufferTextureBindings();

            _rebind = false;
        }

        /// <summary>
        /// Bind respective buffer bindings on the host API.
        /// </summary>
        /// <param name="bufferCache">Buffer cache holding the buffers for the specified ranges</param>
        /// <param name="bindings">Buffer memory ranges to bind</param>
        /// <param name="lastBindings">Bindings currently set on the backend</param>
        /// <param name="isStorage">True to bind as storage buffer, false to bind as uniform buffer</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void BindBuffers(BufferCache bufferCache, BuffersPerStage[] bindings, BufferRange[] lastBindings, bool isStorage)
        {
            int rangesCount = 0;

            Span<BufferAssignment> ranges = _ranges;

            for (ShaderStage stage = ShaderStage.Vertex; stage <= ShaderStage.Fragment; stage++)
            {
                ref var buffers = ref bindings[(int)stage - 1];

                for (int index = 0; index < buffers.Count; index++)
                {
                    ref var bindingInfo = ref buffers.Bindings[index];

                    BufferBounds bounds = buffers.Buffers[bindingInfo.Slot];

                    if (bounds.Address != 0)
                    {
                        int binding = bindingInfo.Binding;

                        BufferRange range = isStorage
                            ? bounds.GetBufferRangeTillEnd(bufferCache)
                            : bounds.GetBufferRange(bufferCache);

                        if (!lastBindings[binding].Equals(range))
                        {
                            lastBindings[binding] = range;
                            ranges[rangesCount++] = new BufferAssignment(binding, range);
                        }
                    }
                }
            }

            if (rangesCount != 0)
            {
                SetHostBuffers(ranges, rangesCount, isStorage);
            }
        }

        /// <summary>
        /// Bind respective buffer bindings on the host API.
        /// </summary>
        /// <param name="bufferCache">Buffer cache holding the buffers for the specified ranges</param>
        /// <param name="buffers">Buffer memory ranges to bind</param>
        /// <param name="isStorage">True to bind as storage buffer, false to bind as uniform buffer</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void BindBuffers(BufferCache bufferCache, BuffersPerStage buffers, bool isStorage)
        {
            int rangesCount = 0;

            Span<BufferAssignment> ranges = _ranges;

            for (int index = 0; index < buffers.Count; index++)
            {
                ref var bindingInfo = ref buffers.Bindings[index];

                BufferBounds bounds = buffers.Buffers[bindingInfo.Slot];

                if (bounds.Address != 0)
                {
                    // TODO: Use prefetched buffers for compute? (_rebind isn't valid in compute, but this compute draw's bindings should have been fetched)
                    var isWrite = bounds.Flags.HasFlag(BufferUsageFlags.Write);
                    var range = isStorage
                        ? bufferCache.GetBufferRangeTillEnd(bounds.Address, bounds.Size, isWrite)
                        : bufferCache.GetBufferRange(bounds.Address, bounds.Size);

                    ranges[rangesCount++] = new BufferAssignment(bindingInfo.Binding, range);
                }
            }

            if (rangesCount != 0)
            {
                SetHostBuffers(ranges, rangesCount, isStorage);
            }
        }

        /// <summary>
        /// Bind respective buffer bindings on the host API.
        /// </summary>
        /// <param name="ranges">Host buffers to bind, with their offsets and sizes</param>
        /// <param name="first">First binding point</param>
        /// <param name="count">Number of bindings</param>
        /// <param name="isStorage">Indicates if the buffers are storage or uniform buffers</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetHostBuffers(ReadOnlySpan<BufferAssignment> ranges, int count, bool isStorage)
        {
            if (isStorage)
            {
                _context.Renderer.Pipeline.SetStorageBuffers(ranges.Slice(0, count));
            }
            else
            {
                _context.Renderer.Pipeline.SetUniformBuffers(ranges.Slice(0, count));
            }
        }

        /// <summary>
        /// Updates data for the already bound buffer bindings.
        /// </summary>
        /// <param name="bindings">Bindings to update</param>
        private void UpdateBuffers(BuffersPerStage[] bindings)
        {
            for (ShaderStage stage = ShaderStage.Vertex; stage <= ShaderStage.Fragment; stage++)
            {
                ref var buffers = ref bindings[(int)stage - 1];

                for (int index = 0; index < buffers.Count; index++)
                {
                    ref var binding = ref buffers.Bindings[index];

                    BufferBounds bounds = buffers.Buffers[binding.Slot];

                    if (bounds.Address == 0)
                    {
                        continue;
                    }

                    bounds.SynchronizeMemory();
                }
            }
        }

        /// <summary>
        /// Sets the buffer storage of a buffer texture. This will be bound when the buffer manager commits bindings.
        /// </summary>
        /// <param name="stage">Shader stage accessing the texture</param>
        /// <param name="texture">Buffer texture</param>
        /// <param name="address">Address of the buffer in memory</param>
        /// <param name="size">Size of the buffer in bytes</param>
        /// <param name="bindingInfo">Binding info for the buffer texture</param>
        /// <param name="format">Format of the buffer texture</param>
        /// <param name="isImage">Whether the binding is for an image or a sampler</param>
        public void SetBufferTextureStorage(
            ShaderStage stage,
            ITexture texture,
            ulong address,
            ulong size,
            TextureBindingInfo bindingInfo,
            Format format,
            bool isImage)
        {
            _channel.MemoryManager.Physical.BufferCache.CreateBuffer(address, size);

            _bufferTextures.Add(new BufferTextureBinding(stage, texture, address, size, bindingInfo, format, isImage));
        }

        /// <summary>
        /// Force all bound textures and images to be rebound the next time CommitBindings is called.
        /// </summary>
        public void Rebind()
        {
            _rebind = true;
        }
    }
}
