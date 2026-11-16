using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace RenderedDocComments.Tests.Fakes
{
    /// <summary>
    /// Runs WPF test code on one shared STA thread. WPF needs STA, its objects belong to the
    /// thread that made them, and the renderer keeps a few (like its text box template) in statics,
    /// so every rendering test uses the same thread, one at a time.
    /// </summary>
    internal static class Sta
    {
        private static readonly BlockingCollection<Action> Queue = new BlockingCollection<Action>();

        static Sta()
        {
            var thread = new Thread(() =>
            {
                foreach (var work in Queue.GetConsumingEnumerable())
                {
                    work();
                }
            })
            {
                IsBackground = true,
                Name = "WPF tests",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public static void Run(Action action)
        {
            ExceptionDispatchInfo error = null;
            using (var done = new ManualResetEventSlim())
            {
                Queue.Add(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        error = ExceptionDispatchInfo.Capture(e);
                    }
                    finally
                    {
                        done.Set();
                    }
                });
                done.Wait();
            }

            error?.Throw();
        }
    }
}
