using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;
using Nito.AsyncEx;
using System.Linq;
using System.Threading;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Immutable;
using System.Runtime.Remoting.Messaging;
using System.Runtime.CompilerServices;

namespace Tests
{
    [ExcludeFromCodeCoverage]
    [TestClass]
    public class RecursiveAsyncLockExample
    {
        // Use a logical call context to track which locks the current async logical stack frame "owns".
        // NOTE: This approach will *only* work on .NET 4.5!
        private static class AsyncLockTracker
        {
            private static readonly string slotName = Guid.NewGuid().ToString("N");

            private static IImmutableDictionary<RecursiveAsyncLock, Tuple<int, Task<IDisposable>>> OwnedLocks
            {
                get
                {
                    var ret = CallContext.LogicalGetData(slotName) as ImmutableDictionary<RecursiveAsyncLock, Tuple<int, Task<IDisposable>>>;
                    if (ret == null)
                        return ImmutableDictionary.Create<RecursiveAsyncLock, Tuple<int, Task<IDisposable>>>();
                    return ret;
                }

                set
                {
                    CallContext.LogicalSetData(slotName, value);
                }
            }

            public static bool Contains(RecursiveAsyncLock mutex)
            {
                return OwnedLocks.ContainsKey(mutex);
            }

            public static Task<IDisposable> Lookup(RecursiveAsyncLock mutex)
            {
                Tuple<int, Task<IDisposable>> value;
                if (OwnedLocks.TryGetValue(mutex, out value))
                    return value.Item2;
                return null;
            }

            public static void Add(RecursiveAsyncLock mutex, Task<IDisposable> key)
            {
                Tuple<int, Task<IDisposable>> value;
                if (!OwnedLocks.TryGetValue(mutex, out value))
                    value = Tuple.Create(0, key);
                OwnedLocks = OwnedLocks.SetItem(mutex, Tuple.Create(value.Item1 + 1, value.Item2));
            }

            public static void Remove(RecursiveAsyncLock mutex)
            {
                var value = OwnedLocks[mutex];
                if (value.Item1 == 1)
                {
                    OwnedLocks = OwnedLocks.Remove(mutex);
                    value.Item2.Result.Dispose();
                }
                else
                {
                    OwnedLocks = OwnedLocks.SetItem(mutex, Tuple.Create(value.Item1 - 1, value.Item2));
                }
            }
        }

        public sealed class RecursiveAsyncLock
        {
            private readonly AsyncLock mutex;

            public RecursiveAsyncLock()
            {
                mutex = new AsyncLock();
            }

            public RecursiveAsyncLock(IAsyncWaitQueue<IDisposable> queue)
            {
                mutex = new AsyncLock(queue);
            }

            public int Id { get { return mutex.Id; } }

            public RecursiveLockAwaitable LockAsync(CancellationToken token)
            {
                var key = AsyncLockTracker.Lookup(this);
                if (key == null)
                    key = mutex.LockAsync(token).AsTask();
                return new RecursiveLockAwaitable(key, this);
            }

            public RecursiveLockAwaitable LockAsync()
            {
                return LockAsync(CancellationToken.None);
            }

            public sealed class RecursiveLockAwaitable : INotifyCompletion
            {
                private readonly Task<IDisposable> _key;
                private readonly TaskAwaiter<IDisposable> _awaiter;
                private readonly RecursiveAsyncLock _mutex;

                public RecursiveLockAwaitable(Task<IDisposable> key, RecursiveAsyncLock mutex)
                {
                    _key = key;
                    _awaiter = key.GetAwaiter();
                    _mutex = mutex;
                }

                public RecursiveLockAwaitable GetAwaiter()
                {
                    return this;
                }

                public bool IsCompleted
                {
                    get { return _awaiter.IsCompleted; }
                }

                public IDisposable GetResult()
                {
                    var ret = _awaiter.GetResult();
                    return new KeyDisposable(_key, _mutex);
                }

                public void OnCompleted(Action continuation)
                {
                    _awaiter.OnCompleted(continuation);
                }

                private sealed class KeyDisposable : IDisposable
                {
                    private RecursiveAsyncLock _mutex;

                    public KeyDisposable(Task<IDisposable> keyTask, RecursiveAsyncLock mutex)
                    {
                        _mutex = mutex;
                        AsyncLockTracker.Add(mutex, keyTask);
                    }

                    public void Dispose()
                    {
                        if (_mutex == null)
                            return;
                        AsyncLockTracker.Remove(_mutex);
                        _mutex = null;
                    }
                }
            }
        }

        [TestMethod]
        public async Task AsyncLock_DoesNotPermitRecursiveWaits()
        {
            var mutex = new AsyncLock();
            var key = await mutex.LockAsync();
            
            var waiter = mutex.LockAsync().AsTask();

            Assert.IsFalse(waiter.Wait(1000));
            key.Dispose();
        }

        [TestMethod]
        public async Task RecursiveAsyncLock_PermitsRecursiveWaits()
        {
            var mutex = new RecursiveAsyncLock();

            Assert.IsFalse(AsyncLockTracker.Contains(mutex));
            var task1 = mutex.LockAsync();
            var key1 = await task1;
            Assert.IsTrue(AsyncLockTracker.Contains(mutex));

            var task2 = mutex.LockAsync();
            var key2 = await task2;
            Assert.IsTrue(AsyncLockTracker.Contains(mutex));

            key2.Dispose();
            Assert.IsTrue(AsyncLockTracker.Contains(mutex));
            key1.Dispose();
            Assert.IsFalse(AsyncLockTracker.Contains(mutex));
        }

        [TestMethod]
        public async Task RecursiveAsyncLock_DoesNotPermitIndependentWaits()
        {
            var mutex0 = new RecursiveAsyncLock();
            using (await mutex0.LockAsync())
            {
                var mutex = new RecursiveAsyncLock();
                Func<TaskCompletionSource, TaskCompletionSource, Task> taker = async (ready, finish) =>
                {
                    Assert.IsFalse(AsyncLockTracker.Contains(mutex));
                    using (await mutex.LockAsync())
                    {
                        Assert.IsTrue(AsyncLockTracker.Contains(mutex));
                        ready.TrySetResult();
                        await finish.Task;
                        Assert.IsTrue(AsyncLockTracker.Contains(mutex));
                    }
                    Assert.IsFalse(AsyncLockTracker.Contains(mutex));
                };

                var ready1 = new TaskCompletionSource();
                var finish1 = new TaskCompletionSource();
                Assert.IsFalse(AsyncLockTracker.Contains(mutex));
                var task1 = taker(ready1, finish1);
                Assert.IsFalse(AsyncLockTracker.Contains(mutex));
                await Task.WhenAny(ready1.Task, task1);
                Assert.IsFalse(AsyncLockTracker.Contains(mutex));

                var ready2 = new TaskCompletionSource();
                var finish2 = new TaskCompletionSource();
                Assert.IsFalse(AsyncLockTracker.Contains(mutex));
                var task2 = taker(ready2, finish2);
                Assert.IsFalse(AsyncLockTracker.Contains(mutex));

                Assert.IsFalse(ready2.Task.Wait(1000));

                finish1.SetResult();
                await Task.WhenAny(ready2.Task, task2);
                Assert.IsFalse(AsyncLockTracker.Contains(mutex));
                finish2.SetResult();
                await Task.WhenAll(task1, task2);
                Assert.IsFalse(AsyncLockTracker.Contains(mutex));
            }
        }

        [TestMethod]
        public void RecursiveAsyncLock_DoesPermitIndependentWaits_WhenUsedInSyncContext()
        {
            var mutex = new RecursiveAsyncLock();

            // acquire lock in sync method
            LockSync(mutex);

            // then anyone would be able to acquire the lock again
            var thread = new Thread(() => LockAsync(mutex).GetAwaiter().GetResult());
            thread.Start();
            thread.Join();
        }

        [TestMethod]
        public async Task RecursiveAsyncLock_DoesPermitMultipleThreads_AtTheSameTime()
        {
            var mutex = new RecursiveAsyncLock();

            await PerformActionUnderLock(
                mutex,
                actionUnderLock: () =>
                {
                    var waitForEverybody = new TaskCompletionSource();

                    // multi-threaded access to the lock; thread safety is not guarantied by this lock.
                    var thread1 = new Thread(() => PerformActionUnderLock(mutex, () => waitForEverybody.Task).GetAwaiter().GetResult());
                    var thread2 = new Thread(() => PerformActionUnderLock(mutex, () => waitForEverybody.Task).GetAwaiter().GetResult());

                    thread1.Start();
                    thread2.Start();

                    Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => waitForEverybody.SetResult());

                    thread1.Join();
                    thread2.Join();

                    return waitForEverybody.Task;
                });
        }

        private static IDisposable LockSync(RecursiveAsyncLock mutex)
        {
            return mutex.LockAsync().GetAwaiter().GetResult();
        }

        private static async Task<IDisposable> LockAsync(RecursiveAsyncLock mutex)
        {
            return await mutex.LockAsync();
        }

        private static async Task PerformActionUnderLock(RecursiveAsyncLock mutex, Func<Task> actionUnderLock)
        {
            using (await mutex.LockAsync())
            {
                await actionUnderLock();
            }
        }
    }
}
