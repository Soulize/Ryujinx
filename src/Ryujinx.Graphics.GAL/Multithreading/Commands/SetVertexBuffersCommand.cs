using Ryujinx.Graphics.GAL.Multithreading.Model;
using System;

namespace Ryujinx.Graphics.GAL.Multithreading.Commands
{
    struct SetVertexBuffersCommand : IGALCommand, IGALCommand<SetVertexBuffersCommand>
    {
        public CommandType CommandType => CommandType.SetVertexBuffers;
        private int _start;
        private SpanRef<VertexBufferDescriptor> _vertexBuffers;

        public void Set(int start, SpanRef<VertexBufferDescriptor> vertexBuffers)
        {
            _start = start;
            _vertexBuffers = vertexBuffers;
        }

        public static void Run(ref SetVertexBuffersCommand command, ThreadedRenderer threaded, IRenderer renderer)
        {
            Span<VertexBufferDescriptor> vertexBuffers = command._vertexBuffers.Get(threaded);
            renderer.Pipeline.SetVertexBuffers(command._start, threaded.Buffers.MapBufferRanges(vertexBuffers));
            command._vertexBuffers.Dispose(threaded);
        }
    }
}
