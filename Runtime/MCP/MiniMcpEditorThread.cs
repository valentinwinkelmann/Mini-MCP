using System;
using System.Collections.Concurrent;
using System.Threading;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MiniMCP
{
    public static class MiniMcpEditorThread
    {
#if UNITY_EDITOR
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();
        private static int mainThreadId;

        static MiniMcpEditorThread()
        {
            EditorApplication.update += DrainQueue;
        }

        [InitializeOnLoadMethod]
        private static void InitializeOnEditorMainThread()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }
#endif

        public static bool Invoke(Action action, TimeSpan timeout, out string error)
        {
            error = string.Empty;
            if (action == null)
            {
                error = "No action provided.";
                return false;
            }

#if UNITY_EDITOR
            if (mainThreadId != 0 && Thread.CurrentThread.ManagedThreadId == mainThreadId)
            {
                try
                {
                    action();
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message ?? "Unknown main-thread execution error.";
                    return false;
                }
            }

            using (ManualResetEventSlim done = new ManualResetEventSlim(false))
            {
                Exception dispatchException = null;
                Queue.Enqueue(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        dispatchException = ex;
                    }
                    finally
                    {
                        done.Set();
                    }
                });

                if (!done.Wait(timeout))
                {
                    error = "Timed out waiting for main thread dispatch.";
                    return false;
                }

                if (dispatchException != null)
                {
                    error = dispatchException.Message ?? "Unknown main-thread execution error.";
                    return false;
                }
            }

            return true;
#else
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message ?? "Unknown execution error.";
                return false;
            }
#endif
        }

#if UNITY_EDITOR
        private static void DrainQueue()
        {
            if (mainThreadId == 0)
            {
                mainThreadId = Thread.CurrentThread.ManagedThreadId;
            }

            while (Queue.TryDequeue(out Action action))
            {
                action?.Invoke();
            }
        }
#endif
    }
}