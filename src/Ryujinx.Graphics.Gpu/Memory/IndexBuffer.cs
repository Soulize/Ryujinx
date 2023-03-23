using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;

namespace Ryujinx.Graphics.Gpu.Memory
{
    /// <summary>
    /// GPU Index Buffer information.
    /// </summary>
    struct IndexBuffer
    {
        public BufferBounds Bounds;
        public IndexType Type;

        public static bool TranslateAndCreateBuffer(MemoryManager memoryManager, ref IndexBuffer ib, ulong gpuVa, ulong size, IndexType type)
        {
            ref BufferBounds bounds = ref ib.Bounds;

            if (BufferBounds.TranslateAndCreateBuffer(memoryManager, ref bounds, gpuVa, size, BufferUsageFlags.None) || ib.Type != type)
            {
                ib.Type = type;

                return true;
            }

            return false;
        }
    }
}