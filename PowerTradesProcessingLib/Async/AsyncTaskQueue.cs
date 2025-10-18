using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PowerTradesProcessing.Async
{
    public enum TaskPriority
    {
        High = 0,
        Normal = 1,
        Low = 2
    }

    public enum TaskResoult
    {
        Queued,
        Delayed,
        Reenqueued,
        Completed,
        Failed
    }

    public interface ICustomLogger<T>
    {
        void Log(T result, string message);
    }

    /// <summary>
    /// Async prioritized task queue with soft backpressure, retry and optional timeout.
    /// </summary>
    public class AsyncTaskQueue : IDisposable
    {
        private readonly SortedDictionary<TaskPriority, Queue<Func<CancellationToken, Task>>> _taskQueues = new();
        private readonly Queue<Func<CancellationToken, Task>> _delayedTasks = new();
        private readonly ManualResetEventSlim _signal = new(false);
        private readonly CancellationTokenSource _cts = new();
        private readonly object _locker = new();
        private readonly ICustomLogger<TaskResoult>? _logger;
        private readonly Task _worker;

        public Guid Tag { get; }
        public int MaxQueueLength { get; set; } = 100;
        public int MaxRetryAttempts { get; set; } = 3;
        public TimeSpan TaskTimeout { get; set; } = TimeSpan.FromSeconds(15);

        public AsyncTaskQueue(ICustomLogger<TaskResoult>? logger = null, Guid? tag = null)
        {
            _logger = logger;
            Tag = tag ?? Guid.NewGuid();
            foreach (TaskPriority priority in Enum.GetValues(typeof(TaskPriority)))
                _taskQueues[priority] = new Queue<Func<CancellationToken, Task>>();

            _worker = Task.Run(ProcessQueueAsync);
        }

        public void Enqueue(Func<CancellationToken, Task> taskFunc, TaskPriority priority)
        {
            if (taskFunc is null) throw new ArgumentNullException(nameof(taskFunc));

            lock (_locker)
            {
                if (GetTotalQueueLength() >= MaxQueueLength)
                {
                    _delayedTasks.Enqueue(taskFunc);
                    _logger?.Log(TaskResoult.Delayed, $"[Queue {Tag}] Delayed task enqueued (queue limit reached).");
                }
                else
                {
                    _taskQueues[priority].Enqueue(taskFunc);
                    _logger?.Log(TaskResoult.Queued, $"[Queue {Tag}] Task enqueued at {priority} priority.");
                }

                _signal.Set();
            }
        }

        private async Task ProcessQueueAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                Func<CancellationToken, Task>? taskToExecute = null;

                lock (_locker)
                {
                    foreach (var kvp in _taskQueues)
                    {
                        if (kvp.Value.Count > 0)
                        {
                            taskToExecute = kvp.Value.Dequeue();
                            break;
                        }
                    }

                    if (taskToExecute == null)
                        _signal.Reset();

                    if (_delayedTasks.Count > 0 && GetTotalQueueLength() < MaxQueueLength)
                    {
                        var delayedTask = _delayedTasks.Dequeue();
                        _taskQueues[TaskPriority.Normal].Enqueue(delayedTask);
                        _logger?.Log(TaskResoult.Reenqueued, $"[Queue {Tag}] Reenqueued delayed task.");
                        _signal.Set();
                    }
                }

                if (taskToExecute is not null)
                {
                    await ExecuteWithRetryAsync(taskToExecute);
                }
                else
                {
                    try { _signal.Wait(_cts.Token); } catch { /* cancelled */ }
                }
            }
        }

        private async Task ExecuteWithRetryAsync(Func<CancellationToken, Task> taskFunc)
        {
            for (int attempt = 1; attempt <= MaxRetryAttempts; attempt++)
            {
                using var timeoutCts = new CancellationTokenSource(TaskTimeout);
                try
                {
                    await taskFunc(timeoutCts.Token);
                    _logger?.Log(TaskResoult.Completed, $"[Queue {Tag}] Task completed (attempt {attempt}).");
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt >= MaxRetryAttempts)
                        _logger?.Log(TaskResoult.Failed, $"[Queue {Tag}] Task failed after {attempt} attempts: {ex.Message}");
                    else
                        _logger?.Log(TaskResoult.Failed, $"[Queue {Tag}] Retry {attempt} failed: {ex.Message}");
                }
            }
        }

        private int GetTotalQueueLength()
        {
            return _taskQueues.Values.Sum(q => q.Count) + _delayedTasks.Count;
        }

        public int PendingCount
        {
            get { lock (_locker) return GetTotalQueueLength(); }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _signal.Set();
            try { _worker.Wait(5000); } catch { /* swallow timeout */ }
            _cts.Dispose();
            _signal.Dispose();
        }
    }
}

