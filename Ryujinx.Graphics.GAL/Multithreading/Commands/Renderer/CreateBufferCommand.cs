using Ryujinx.Graphics.GAL.Multithreading.Resources;
using Ryujinx.Graphics.Shader;

namespace Ryujinx.Graphics.GAL.Multithreading.Commands.Renderer
{
    struct CreateBufferCommand : IGALCommand
    {
        public CommandType CommandType => CommandType.CreateBuffer;
        private BufferHandle _threadedHandle;
        private int _size;
        private BufferAccess _access;

        public void Set(BufferHandle threadedHandle, int size, BufferAccess access)
        {
            _threadedHandle = threadedHandle;
            _size = size;
            _access = access;
        }

        public static void Run(ref CreateBufferCommand command, ThreadedRenderer threaded, IRenderer renderer)
        {
            threaded.Buffers.AssignBuffer(command._threadedHandle, renderer.CreateBuffer(command._size, command._access));
        }
    }
}
