using Ryujinx.Graphics.Shader;

namespace Ryujinx.Graphics.Gpu.Memory
{
    /// <summary>
    /// GPU Vertex Buffer information.
    /// </summary>
    struct VertexBuffer
    {
        public BufferBounds Bounds;
        public int   Stride;
        public int   Divisor;

        public static bool TranslateAndCreateBuffer(MemoryManager memoryManager, ref VertexBuffer vb, ulong gpuVa, ulong size, int stride, int divisor)
        {
            ref BufferBounds bounds = ref vb.Bounds;

            if (BufferBounds.TranslateAndCreateBuffer(memoryManager, ref bounds, gpuVa, size, BufferUsageFlags.None) || vb.Stride != stride || vb.Divisor != divisor)
            {
                vb.Stride = stride;
                vb.Divisor = divisor;

                return true;
            }

            return false;
        }
    }
}