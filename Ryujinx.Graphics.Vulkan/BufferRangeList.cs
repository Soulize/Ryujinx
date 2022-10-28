﻿using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Vulkan
{
    struct BufferRangeList
    {
        internal struct Range
        {
            public int Offset { get; }
            public int Size { get; }

            public Range(int offset, int size)
            {
                Offset = offset;
                Size = size;
            }

            public bool OverlapsWith(int offset, int size)
            {
                return Offset < offset + size && offset < Offset + Size;
            }
        }

        private List<Range> _ranges;

        public List<Range> All()
        {
            return _ranges;
        }

        public bool Remove(int offset, int size)
        {
            var list = _ranges;
            bool removedAny = false;
            if (list != null)
            {
                int overlapIndex = BinarySearch(list, offset, size);

                if (overlapIndex >= 0)
                {
                    // Overlaps with a range. Search back to find the first one it doesn't overlap with.

                    while (overlapIndex > 0 && list[overlapIndex - 1].OverlapsWith(offset, size))
                    {
                        overlapIndex--;
                    }

                    int endOffset = offset + size;
                    int startIndex = overlapIndex;

                    var startOverlap = list[overlapIndex];
                    if (startOverlap.Offset < offset)
                    {
                        list[overlapIndex++] = new Range(startOverlap.Offset, offset - startOverlap.Offset);
                        startIndex++;

                        removedAny = true;
                    }

                    while (overlapIndex < list.Count && list[overlapIndex].OverlapsWith(offset, size))
                    {
                        var currentOverlap = list[overlapIndex];
                        var currentOverlapEndOffset = currentOverlap.Offset + currentOverlap.Size;

                        if (currentOverlapEndOffset > endOffset)
                        {
                            list[overlapIndex] = new Range(endOffset, currentOverlapEndOffset - endOffset);

                            removedAny = true;
                            break;
                        }

                        overlapIndex++;
                    }

                    int count = overlapIndex - startIndex;

                    list.RemoveRange(startIndex, count);

                    removedAny |= count > 0;
                }
            }

            return removedAny;
        }

        public void Add(int offset, int size)
        {
            var list = _ranges;
            if (list != null)
            {
                int overlapIndex = BinarySearch(list, offset, size);
                if (overlapIndex >= 0)
                {
                    while (overlapIndex > 0 && list[overlapIndex - 1].OverlapsWith(offset, size))
                    {
                        overlapIndex--;
                    }

                    int endOffset = offset + size;
                    int startIndex = overlapIndex;

                    while (overlapIndex < list.Count && list[overlapIndex].OverlapsWith(offset, size))
                    {
                        var currentOverlap = list[overlapIndex];
                        var currentOverlapEndOffset = currentOverlap.Offset + currentOverlap.Size;

                        if (offset > currentOverlap.Offset)
                        {
                            offset = currentOverlap.Offset;
                        }

                        if (endOffset < currentOverlapEndOffset)
                        {
                            endOffset = currentOverlapEndOffset;
                        }

                        overlapIndex++;
                    }

                    int count = overlapIndex - startIndex;

                    list.RemoveRange(startIndex, count);

                    size = endOffset - offset;
                    overlapIndex = startIndex;
                }
                else
                {
                    overlapIndex = ~overlapIndex;
                }

                list.Insert(overlapIndex, new Range(offset, size));

                int last = 0;
                foreach (var rg in list)
                {
                    if (rg.Offset < last)
                    {
                        throw new System.Exception("list not properly sorted");
                    }
                    last = rg.Offset;
                }
            }
            else
            {
                _ranges = new List<Range>
                {
                    new Range(offset, size)
                };
            }
        }

        public bool OverlapsWith(int offset, int size)
        {
            var list = _ranges;
            if (list == null)
            {
                return false;
            }

            return BinarySearch(list, offset, size) >= 0;
        }

        private static int BinarySearch(List<Range> list, int offset, int size)
        {
            int left = 0;
            int right = list.Count - 1;

            while (left <= right)
            {
                int range = right - left;

                int middle = left + (range >> 1);

                var item = list[middle];

                if (item.OverlapsWith(offset, size))
                {
                    return middle;
                }

                if (offset < item.Offset)
                {
                    right = middle - 1;
                }
                else
                {
                    left = middle + 1;
                }
            }

            return ~left;
        }

        public void FillData(Span<byte> baseData, Span<byte> modData, int offset, Span<byte> result)
        {
            int size = baseData.Length;
            int endOffset = offset + size;

            var list = _ranges;
            if (list == null)
            {
                baseData.CopyTo(result);
            }

            int srcOffset = offset;
            int dstOffset = 0;
            bool activeRange = false;

            for (int i = 0; i < list.Count; i++)
            {
                var range = list[i];

                int rangeEnd = range.Offset + range.Size;

                if (activeRange)
                {
                    if (range.Offset >= endOffset)
                    {
                        break;
                    }
                }
                else
                {
                    if (rangeEnd <= offset)
                    {
                        continue;
                    }

                    activeRange = true;
                }

                int baseSize = range.Offset - srcOffset;

                if (baseSize > 0)
                {
                    baseData.Slice(dstOffset, baseSize).CopyTo(result.Slice(dstOffset, baseSize));
                    srcOffset += baseSize;
                    dstOffset += baseSize;
                }

                int modSize = Math.Min(rangeEnd - srcOffset, endOffset - srcOffset);
                if (modSize != 0)
                {
                    modData.Slice(dstOffset, modSize).CopyTo(result.Slice(dstOffset, modSize));
                    srcOffset += modSize;
                    dstOffset += modSize;
                }
            }

            int baseSizeEnd = endOffset - srcOffset;

            if (baseSizeEnd > 0)
            {
                baseData.Slice(dstOffset, baseSizeEnd).CopyTo(result.Slice(dstOffset, baseSizeEnd));
            }
        }

        public int Count()
        {
            return _ranges?.Count ?? 0;
        }

        public void Clear()
        {
            _ranges = null;
        }
    }
}
