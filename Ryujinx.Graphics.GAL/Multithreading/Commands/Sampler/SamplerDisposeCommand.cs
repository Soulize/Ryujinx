﻿using Ryujinx.Graphics.GAL.Multithreading.Model;
using Ryujinx.Graphics.GAL.Multithreading.Resources;

namespace Ryujinx.Graphics.GAL.Multithreading.Commands.Sampler
{
    struct SamplerDisposeCommand : IGALCommand
    {
        public CommandType CommandType => CommandType.SamplerDispose;
        private TableRef<ThreadedSampler> _sampler;

        public void Set(TableRef<ThreadedSampler> sampler)
        {
            _sampler = sampler;
        }

        public void Run(ThreadedRenderer threaded, IRenderer renderer)
        {
            _sampler.Get(threaded).Base.Dispose();
        }
    }
}
