using ARMeilleure.Common;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace ARMeilleure.Translation
{
    class TranslatedFunction
    {
        private readonly GuestFunction _func; // Ensure that this delegate will not be garbage collected.
        private List<ulong> _views;

        public Counter<uint> CallCounter { get; }
        public ulong GuestSize { get; }
        public bool HighCq { get; }
        public IntPtr FuncPtr { get; }
        public List<ulong> Views => _views;
        public bool IsView { get; }

        public TranslatedFunction(GuestFunction func, Counter<uint> callCounter, ulong guestSize, bool highCq, bool isView = false)
        {
            _func = func;
            CallCounter = callCounter;
            GuestSize = guestSize;
            HighCq = highCq;
            FuncPtr = Marshal.GetFunctionPointerForDelegate(func);
            IsView = isView;
        }

        public ulong Execute(State.ExecutionContext context)
        {
            return _func(context.NativeContextPtr);
        }

        public TranslatedFunction CreateView(ulong address, ulong size)
        {
            if (_views == null)
            {
                Interlocked.CompareExchange(ref _views, new List<ulong>(), null);
            }

            _views.Add(address);

            return new TranslatedFunction(_func, CallCounter, size, HighCq, isView: true);
        }
    }
}