using Ryujinx.Common;
using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.HLE.HOS.Kernel.Common
{
    /// <summary>
    /// A clock that attempts to synchronize with the host clock at millisecond granularity.
    /// </summary>
    internal class MillisecondClock : IDisposable
    {
        private static bool ReduceLongSleepAccuracy = true;

        private static long OneMillisecond = Stopwatch.Frequency / 1000;
        private static long LateAllowance = Stopwatch.Frequency / 25000; // 0.04ms
        private static long InaccurateSleepThreshold = Stopwatch.Frequency / 500; // >2ms
        private static long ClockErrorThreshold = Stopwatch.Frequency / 666; // >1.5ms

        private ManualResetEvent _event;
        private AutoResetEvent _msSignal;
        private ulong _currentMs;
        private long _lastMsTick;
        private object _lock = new object();

        private bool _running;
        private Thread _timerThread;

        /// <summary>
        /// Create a new MillisecondClock.
        /// </summary>
        public MillisecondClock()
        {
            _event = new ManualResetEvent(false);
            _msSignal = new AutoResetEvent(false);
            _lastMsTick = PerformanceCounter.ElapsedTicks;

            _running = true;
            _timerThread = new Thread(Loop);
            _timerThread.Name = "HLE.TimeManager.Sleep";

            _timerThread.Start();
        }

        /// <summary>
        /// Track when milliseconds pass and signal.
        /// </summary>
        public void Loop()
        {
            while (_running)
            {
                _event.WaitOne(1);

                lock (_lock)
                {
                    long tick = PerformanceCounter.ElapsedTicks;

                    if (tick - _lastMsTick > ClockErrorThreshold)
                    {
                        // Increase the current ms by the difference.
                        _currentMs += (ulong)Math.Round((tick - _lastMsTick) / (Stopwatch.Frequency / 1000.0));
                    }
                    else
                    {
                        _currentMs++;
                    }

                    _lastMsTick = tick;
                }

                _msSignal.Set();
            }
        }

        /// <summary>
        /// Interrupt the current wait.
        /// </summary>
        public void Interrupt()
        {
            _msSignal.Set();
        }

        /// <summary>
        /// Wait for the specified millisecond or a signal.
        /// </summary>
        /// <param name="millisecond">Millisecond to wait for</param>
        /// <returns>True if the target was met, false if interrupted early</returns>
        public bool WaitForMs(ulong millisecond)
        {
            if (millisecond > _currentMs)
            {
                _msSignal.WaitOne();
            }

            return millisecond <= _currentMs;
        }


        /// <summary>
        /// Wait for a signal.
        /// </summary>
        public void WaitAny()
        {
            _msSignal.WaitOne();
        }

        /// <summary>
        /// Get a target millisecond to wait until for a given timepoint.
        /// </summary>
        /// <param name="timepoint">The timepoint to wait until</param>
        /// <returns>The target millisecond</returns>
        public ulong GetTargetMs(long timepoint)
        {
            ulong currentMs;
            long lastMsTick;

            lock (_lock)
            {
                currentMs = _currentMs;
                lastMsTick = _lastMsTick;
            }

            long difference = timepoint - lastMsTick;

            if (difference < 0)
            {
                return currentMs;
            }

            if (ReduceLongSleepAccuracy && difference > InaccurateSleepThreshold)
            {
                // Round to nearest, but with a bias towards waiting longer.

                return currentMs + (ulong)Math.Round((difference / (Stopwatch.Frequency / 1000.0)) + 0.25);
            }
            else
            {
                long remainder = difference % OneMillisecond;

                // Allow timepoints to be missed by a small amount.

                if (remainder > OneMillisecond - LateAllowance)
                {
                    return currentMs + (ulong)((difference + OneMillisecond - 1) / OneMillisecond);
                }

                return currentMs + (ulong)(difference / OneMillisecond);
            }
        }

        /// <summary>
        /// Dispose the millisecond clock.
        /// </summary>
        public void Dispose()
        {
            _running = false;
            _timerThread.Join();

            _event.Dispose();
            _msSignal.Dispose();
        }
    }
}
