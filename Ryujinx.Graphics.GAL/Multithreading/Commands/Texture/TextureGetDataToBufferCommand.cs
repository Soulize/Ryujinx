using Ryujinx.Graphics.GAL.Multithreading.Model;
using Ryujinx.Graphics.GAL.Multithreading.Resources;

namespace Ryujinx.Graphics.GAL.Multithreading.Commands.Texture
{
    struct TextureGetDataToBufferCommand : IGALCommand
    {
        public CommandType CommandType => CommandType.TextureGetDataToBuffer;
        private TableRef<ThreadedTexture> _texture;
        private BufferRange _range;
        private int _layer;
        private int _level;

        public void Set(TableRef<ThreadedTexture> texture, BufferRange range, int layer, int level)
        {
            _texture = texture;
            _range = range;
            _layer = layer;
            _level = level;
        }

        public static void Run(ref TextureGetDataToBufferCommand command, ThreadedRenderer threaded, IRenderer renderer)
        {
            command._texture.Get(threaded).Base.GetData(threaded.Buffers.MapBufferRange(command._range), command._layer, command._level);
        }
    }
}
