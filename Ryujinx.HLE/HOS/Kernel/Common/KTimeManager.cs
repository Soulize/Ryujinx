using Ryujinx.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Ryujinx.HLE.HOS.Kernel.Common
{
    class KTimeManager : IDisposable
    {
        public static readonly long DefaultTimeIncrementNanoseconds = ConvertGuestTicksToNanoseconds(2);

        private class WaitingObject
        {
            public IKFutureSchedulerObject Object { get; }
            public long TimePoint { get; }
            public ulong Millisecond { get; }

            public WaitingObject(IKFutureSchedulerObject schedulerObj, long timePoint, ulong millisecond)
            {
                Object = schedulerObj;
                TimePoint = timePoint;
                Millisecond = millisecond;
            }
        }

        private readonly KernelContext _context;
        private readonly List<WaitingObject> _waitingObjects;
        private AutoResetEvent _waitEvent;
        private bool _keepRunning;
        private long _enforceWakeupFromSpinWait;

        private MillisecondClock _clock;

        public KTimeManager(KernelContext context)
        {
            _context = context;
            _waitingObjects = new List<WaitingObject>();
            _keepRunning = true;
            _clock = new MillisecondClock();

            Thread work = new Thread(WaitAndCheckScheduledObjects)
            {
                Name = "HLE.TimeManager"
            };

            work.Start();
        }

        public void ScheduleFutureInvocation(IKFutureSchedulerObject schedulerObj, long timeout)
        {
            long timePoint = PerformanceCounter.ElapsedTicks + ConvertNanosecondsToHostTicks(timeout);

            lock (_context.CriticalSection.Lock)
            {
                _waitingObjects.Add(new WaitingObject(schedulerObj, timePoint, _clock.GetTargetMs(timePoint)));

                if (timeout < 1000000)
                {
                    Interlocked.Exchange(ref _enforceWakeupFromSpinWait, 1);
                }
            }

            _clock.Interrupt();
        }

        public void UnscheduleFutureInvocation(IKFutureSchedulerObject schedulerObj)
        {
            lock (_context.CriticalSection.Lock)
            {
                _waitingObjects.RemoveAll(x => x.Object == schedulerObj);
            }
        }

        private void WaitAndCheckScheduledObjects()
        {
            SpinWait spinWait = new SpinWait();
            WaitingObject next;

            while (_keepRunning)
            {
                lock (_context.CriticalSection.Lock)
                {
                    Interlocked.Exchange(ref _enforceWakeupFromSpinWait, 0);

                    next = _waitingObjects.OrderBy(x => x.TimePoint).FirstOrDefault();
                }

                if (next != null)
                {
                    long timePoint = PerformanceCounter.ElapsedTicks;

                    if (next.TimePoint > timePoint)
                    {
                        if (_clock.WaitForMs(next.Millisecond))
                        {
                            // If waiting returns true, not long is left until the timepoint. Spin for the remainder.

                            long remaining = next.TimePoint - PerformanceCounter.ElapsedTicks;

                            while (Interlocked.Read(ref _enforceWakeupFromSpinWait) != 1 && PerformanceCounter.ElapsedTicks <= next.TimePoint)
                            {
                                if (spinWait.NextSpinWillYield)
                                {
                                    Thread.Yield();

                                    spinWait.Reset();
                                }

                                spinWait.SpinOnce();
                            }

                            spinWait.Reset();
                        }
                    }

                    bool timeUp = PerformanceCounter.ElapsedTicks >= next.TimePoint;

                    if (timeUp)
                    {
                        lock (_context.CriticalSection.Lock)
                        {
                            if (_waitingObjects.Remove(next))
                            {
                                next.Object.TimeUp();
                            }
                        }
                    }
                }
                else
                {
                    _clock.WaitAny();
                }
            }
        }

        public static long ConvertNanosecondsToMilliseconds(long time)
        {
            time /= 1000000;

            if ((ulong)time > int.MaxValue)
            {
                return int.MaxValue;
            }

            return time;
        }

        public static long ConvertMillisecondsToNanoseconds(long time)
        {
            return time * 1000000;
        }

        public static long ConvertNanosecondsToHostTicks(long ns)
        {
            long nsDiv = ns / 1000000000;
            long nsMod = ns % 1000000000;
            long tickDiv = PerformanceCounter.TicksPerSecond / 1000000000;
            long tickMod = PerformanceCounter.TicksPerSecond % 1000000000;

            long baseTicks = (nsMod * tickMod + PerformanceCounter.TicksPerSecond - 1) / 1000000000;
            return (nsDiv * tickDiv) * 1000000000 + nsDiv * tickMod + nsMod * tickDiv + baseTicks;
        }

        public static long ConvertGuestTicksToNanoseconds(long ticks)
        {
            return (long)Math.Ceiling(ticks * (1000000000.0 / 19200000.0));
        }

        public static long ConvertHostTicksToTicks(long time)
        {
            return (long)((time / (double)PerformanceCounter.TicksPerSecond) * 19200000.0);
        }

        public void Dispose()
        {
            _keepRunning = false;
            _clock.Interrupt();
            _clock.Dispose();
        }
    }
}