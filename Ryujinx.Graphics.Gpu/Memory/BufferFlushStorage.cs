using Ryujinx.Common.Pools;
using Ryujinx.Graphics.GAL;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ryujinx.Graphics.Gpu.Memory
{
    class BufferFlushableRange : BufferModifiedRange, INonOverlappingRange
    {
        public BufferFlushableRange(ulong address, ulong size, ulong syncNumber) : base(address, size, syncNumber)
        {
        }

        public INonOverlappingRange Split(ulong splitAddress)
        {
            BufferFlushableRange newRegion = new BufferFlushableRange(splitAddress, EndAddress - splitAddress, SyncNumber);
            Size = splitAddress - Address;

            return newRegion;
        }
    }

    /// <summary>
    /// This structure keeps track of 
    /// </summary>
    internal class BufferFlushStorage : IDisposable
    {
        private BufferHandle _flushBuffer;
        private GpuContext _context;
        private Buffer _parent;
        private bool _disposed;

        private NonOverlappingRangeList<BufferFlushableRange> _flushable;

        public BufferFlushStorage(GpuContext context, Buffer parent)
        {
            _context = context;
            _parent = parent;

            _flushable = new NonOverlappingRangeList<BufferFlushableRange>();
        }

        public void TryCopy(ulong offset, ulong size, ulong syncNumber)
        {
            // If the given range is represented on the flushable list, copy the data and update the sync number.
            // It's possible it only partially exists - in that case, copy the flushable subregions only.

            if (_flushBuffer == BufferHandle.Null)
            {
                _flushBuffer = _context.Renderer.CreateBuffer((int)_parent.Size, BufferAccess.FlushPersistent);
            }

            ref var overlaps = ref ThreadStaticArray<BufferFlushableRange>.Get();

            int overlapCount = _flushable.FindOverlapsNonOverlapping(offset, size, ref overlaps);

            ulong start = 0;
            ulong end = 0;
            for (int i = 0; i < overlapCount; i++)
            {
                var overlap = overlaps[i];

                if (overlap.SyncNumber != syncNumber)
                {
                    if (end == overlap.Address)
                    {
                        end += overlap.Size;
                    }
                    else
                    {
                        // If there's a range, copy it.

                        if (start != end)
                        {
                            _context.Renderer.Pipeline.CopyBuffer(_parent.Handle, _flushBuffer, (int)start, (int)start, (int)(end - start));
                        }

                        start = overlap.Address;
                        end = start + overlap.Size;
                    }

                    overlap.SyncNumber = syncNumber;
                }
            }

            if (start != end)
            {
                _context.Renderer.Pipeline.CopyBuffer(_parent.Handle, _flushBuffer, (int)start, (int)start, (int)(end - start));
            }
        }

        public bool TryFlush(ulong offset, ulong size, ulong syncNumber, out ReadOnlySpan<byte> data)
        {
            // To copy from the flush buffer:
            // - The range must be fully represented on the flushable range list, and copied to at least the requested sync number.

            List<BufferFlushableRange> result = new List<BufferFlushableRange>();
            _flushable.GetOrAddRegions(result, offset, size, (address, size) =>
            {
                return new BufferFlushableRange(address, size, 0);
            });

            bool canFlush = !_disposed;
            if (canFlush)
            {
                foreach (var region in result)
                {
                    if (region.SyncNumber == 0)
                    {
                        canFlush = false;
                        break;
                    }
                }
            }

            if (canFlush)
            {
                data = _context.Renderer.GetBufferData(_flushBuffer, (int)offset, (int)size);
            }
            else
            {
                data = ReadOnlySpan<byte>.Empty;
            }

            return canFlush;
        }

        public void Dispose()
        {
            _disposed = true;
            if (_flushBuffer != BufferHandle.Null)
            {
                _context.Renderer.DeleteBuffer(_flushBuffer);
            }
        }
    }
}
