// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;

namespace System.Threading.Tasks
{
    //
    // TaskReplicator runs a delegate inside of one or more Tasks, concurrently.  The idea is to exploit "available"
    // parallelism, where "available" is determined by the TaskScheduler.  We always keep one Task queued to
    // the scheduler, and if it starts running we queue another one, etc., up to some (potentially) user-defined
    // limit.
    //
    internal sealed class TaskReplicator
    {
        public delegate void ReplicatableUserAction<TState>(ref TState replicaState, long timeout, out bool yieldedBeforeCompletion);

        private readonly TaskScheduler _scheduler;
        private readonly bool _stopOnFirstFailure;

        private readonly ConcurrentQueue<Replica> _pendingReplicas = new ConcurrentQueue<Replica>();
        private ConcurrentQueue<Exception>? _exceptions;
        private bool _stopReplicating;

        private abstract class Replica
        {
            protected readonly TaskReplicator _replicator;
            protected readonly long _timeout;
            protected int _remainingConcurrency;
            protected volatile Task? _pendingTask; // the most recently queued Task for this replica, or null if we're done.

            protected Replica(TaskReplicator replicator, int maxConcurrency, long timeout)
            {
                _replicator = replicator;
                _timeout = timeout;
                _remainingConcurrency = maxConcurrency - 1;
                _pendingTask = new Task(s => ((Replica)s!).Execute(), this);
                _replicator._pendingReplicas.Enqueue(this);
            }

            public void Start()
            {
                _pendingTask!.RunSynchronously(_replicator._scheduler);
            }

            public void Wait()
            {
                //
                // We wait in a loop because each Task might queue another Task, and so on.
                // It's entirely possible for multiple Tasks to be queued without this loop seeing them,
                // but that's fine, since we really only need to know when all of them have finished.
                //
                // Note that it's *very* important that we use Task.Wait here, rather than waiting on some
                // other synchronization primitive.  Task.Wait can "inline" the Task's execution, on this thread,
                // if it hasn't started running on another thread.  That's essential for preventing deadlocks,
                // in the case where all other threads are blocked for other reasons.
                //
                Task? pendingTask;
                while ((pendingTask = _pendingTask) != null)
                    pendingTask.Wait();
            }

            public void Execute()
            {
                try
                {
                    if (!_replicator._stopReplicating && _remainingConcurrency > 0)
                    {
                        CreateNewReplica();
                        _remainingConcurrency = 0; // new replica is responsible for adding concurrency from now on.
                    }

                    bool userActionYieldedBeforeCompletion;

                    ExecuteAction(out userActionYieldedBeforeCompletion);

                    if (userActionYieldedBeforeCompletion)
                    {
                        _pendingTask = new Task(s => ((Replica)s!).Execute(), this, CancellationToken.None, TaskCreationOptions.None);
                        _pendingTask.Start(_replicator._scheduler);
                    }
                    else
                    {
                        _replicator._stopReplicating = true;
                        _pendingTask = null;
                    }
                }
                catch (Exception ex)
                {
                    LazyInitializer.EnsureInitialized(ref _replicator._exceptions).Enqueue(ex);
                    if (_replicator._stopOnFirstFailure)
                        _replicator._stopReplicating = true;
                    _pendingTask = null;
                }
            }

            protected abstract void CreateNewReplica();
            protected abstract void ExecuteAction(out bool yieldedBeforeCompletion);
        }

        private sealed class Replica<TState> : Replica
        {
            private readonly ReplicatableUserAction<TState> _action;
            private TState _state = default!;

            public Replica(TaskReplicator replicator, int maxConcurrency, long timeout, ReplicatableUserAction<TState> action)
                : base(replicator, maxConcurrency, timeout)
            {
                _action = action;
            }

            protected override void CreateNewReplica()
            {
                Replica<TState> newReplica = new Replica<TState>(_replicator, _remainingConcurrency, GenerateCooperativeMultitaskingTaskTimeout(), _action);
                newReplica._pendingTask!.Start(_replicator._scheduler);
            }

            protected override void ExecuteAction(out bool yieldedBeforeCompletion)
            {
                _action(ref _state, _timeout, out yieldedBeforeCompletion);
            }
        }

        private TaskReplicator(ParallelOptions options, bool stopOnFirstFailure)
        {
            _scheduler = options.TaskScheduler ?? TaskScheduler.Current;
            _stopOnFirstFailure = stopOnFirstFailure;
        }

        public static void Run<TState>(ReplicatableUserAction<TState> action, ParallelOptions options, bool stopOnFirstFailure)
        {
            // Browser hosts do not support synchronous Wait so we want to run the
            //  replicated task directly instead of going through Task infrastructure
#if !FEATURE_WASM_MANAGED_THREADS
            if (OperatingSystem.IsBrowser() || OperatingSystem.IsWasi() )
            {
                // Since we are running on a single thread, we don't want the action to time out
                long timeout = long.MaxValue - 1;
                var state = default(TState)!;

                action(ref state, timeout, out bool yieldedBeforeCompletion);
                if (yieldedBeforeCompletion)
                    throw new Exception("Replicated tasks cannot yield in this single-threaded browser environment");
            }
            else
#endif
            {
                int maxConcurrencyLevel = (options.EffectiveMaxConcurrencyLevel > 0) ? options.EffectiveMaxConcurrencyLevel : int.MaxValue;

                TaskReplicator replicator = new TaskReplicator(options, stopOnFirstFailure);
                new Replica<TState>(replicator, maxConcurrencyLevel, timeout: long.MaxValue, action).Start();

                Replica? nextReplica;
                while (replicator._pendingReplicas.TryDequeue(out nextReplica))
                    nextReplica.Wait();

                if (replicator._exceptions != null)
                    throw new AggregateException(replicator._exceptions);
            }
        }

        private static int GenerateCooperativeMultitaskingTaskTimeout()
        {
            // This logic ensures that we have a diversity of timeouts in the range [100 ms, 100 + 50 * ProcessorCount ms) across worker tasks.
            // Otherwise all workers will try to timeout at precisely the same point, which is bad if the work is just about to finish.
            // These 100/50 values are somewhat arbitrary.
            return 100 + Random.Shared.Next(0, 50 * Environment.ProcessorCount);
        }
    }
}
