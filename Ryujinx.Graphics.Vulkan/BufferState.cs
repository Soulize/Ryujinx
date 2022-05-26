using Silk.NET.Vulkan;
using System;

namespace Ryujinx.Graphics.Vulkan
{
    struct BufferState : IDisposable
    {
        public static BufferState Null => new BufferState(null, 0, 0);

        private readonly Auto<DisposableBuffer> _buffer;
        private readonly int _offset;
        private readonly int _size;
        private readonly ulong _stride;
        private readonly IndexType _type;

        public BufferState(Auto<DisposableBuffer> buffer, int offset, int size, IndexType type)
        {
            _buffer = buffer;
            _offset = offset;
            _size = size;
            _stride = 0;
            _type = type;
            buffer?.IncrementReferenceCount();
        }

        public BufferState(Auto<DisposableBuffer> buffer, int offset, int size, ulong stride = 0UL)
        {
            _buffer = buffer;
            _offset = offset;
            _size = size;
            _stride = stride;
            _type = IndexType.Uint16;
            buffer?.IncrementReferenceCount();
        }

        public void BindIndexBuffer(Vk api, CommandBufferScoped cbs)
        {
            if (_buffer != null)
            {
                int offset = _offset;
                DisposableBuffer buffer = _buffer.GetMirrorable(cbs, ref offset, _size);

                api.CmdBindIndexBuffer(cbs.CommandBuffer, buffer.Value, (ulong)offset, _type);
            }
        }

        public void BindTransformFeedbackBuffer(VulkanRenderer gd, CommandBufferScoped cbs, uint binding)
        {
            if (_buffer != null)
            {
                var buffer = _buffer.Get(cbs, _offset, _size, true).Value;

                gd.TransformFeedbackApi.CmdBindTransformFeedbackBuffers(cbs.CommandBuffer, binding, 1, buffer, (ulong)_offset, (ulong)_size);
            }
        }

        public void BindVertexBuffer(VulkanRenderer gd, CommandBufferScoped cbs, uint binding)
        {
            if (_buffer != null)
            {
                int offset = _offset;
                var buffer = _buffer.GetMirrorable(cbs, ref offset, _size).Value;

                if (gd.Capabilities.SupportsExtendedDynamicState)
                {
                    gd.ExtendedDynamicStateApi.CmdBindVertexBuffers2(
                        cbs.CommandBuffer,
                        binding,
                        1,
                        buffer,
                        (ulong)offset,
                        (ulong)_size,
                        _stride);
                }
                else
                {
                    gd.Api.CmdBindVertexBuffers(cbs.CommandBuffer, binding, 1, buffer, (ulong)offset);
                }
            }
        }

        public bool Overlaps(Auto<DisposableBuffer> buffer, int offset, int size)
        {
            return buffer == _buffer && offset < _offset + _size && offset + size > _offset;
        }

        public void Dispose()
        {
            _buffer?.DecrementReferenceCount();
        }
    }
}
