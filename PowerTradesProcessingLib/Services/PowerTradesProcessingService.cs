using System;
using System.Threading;
using System.Threading.Tasks;
using PowerTradesProcessing.Async;

namespace PowerTradesProcessing.Services
{
    public enum LoaderStatus
    {
        Idle,
        Queued,
        Running,
        Draining,
        Stopped
    }

    public sealed class PowerTradesProcessingService : IDisposable
    {
        private AsyncTaskQueue _queue;
        private readonly object _locker = new();
        private LoaderStatus _status = LoaderStatus.Idle;
        private int _refCount = 0;
        private bool _disposed = false;
        private readonly ICustomLogger<TaskResoult>? _logger;
        private readonly Guid? _tag;
        private int _disposeScheduled = 0;

        private static readonly object s_lock = new();
        private static PowerTradesProcessingService _instance;

        public static PowerTradesProcessingService Instance
        {
            get
            {
                lock (s_lock)
                {
                    return _instance ??= new PowerTradesProcessingService();
                }
            }
        }

        public LoaderStatus Status { get { lock (_locker) return _status; } private set { lock (_locker) _status = value; } }
        public int Pending => EnsureQueue().PendingCount;
        public Guid Tag => EnsureQueue().Tag;

        public TimeSpan TaskTimeout { get => EnsureQueue().TaskTimeout; set => EnsureQueue().TaskTimeout = value; }
        public int MaxRetryAttempts { get => EnsureQueue().MaxRetryAttempts; set => EnsureQueue().MaxRetryAttempts = value; }
        public int MaxQueueLength { get => EnsureQueue().MaxQueueLength; set => EnsureQueue().MaxQueueLength = value; }

        private PowerTradesProcessingService(ICustomLogger<TaskResoult>? logger = null, Guid? tag = null)
        {
            _logger = logger;
            _tag = tag;
            _queue = new AsyncTaskQueue(logger, tag);
        }

        public static PowerTradesProcessingService Initialize(ICustomLogger<TaskResoult>? logger = null, Guid? tag = null)
        {
            lock (s_lock)
            {
                if (_instance == null)
                    _instance = new PowerTradesProcessingService(logger, tag);
                return _instance;
            }
        }

        private AsyncTaskQueue EnsureQueue()
        {
            if (_disposed || _queue == null)
            {
                _queue = new AsyncTaskQueue(_logger, _tag);
                _disposed = false;
            }
            return _queue;
        }

        public void Acquire()
        {
            Interlocked.Increment(ref _refCount);
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) <= 0)
                ScheduleDisposeIfIdle();
        }

        private void ScheduleDisposeIfIdle()
        {
            if (Interlocked.Exchange(ref _disposeScheduled, 1) == 1)
                return; // already scheduled

            _ = Task.Run(async () =>
            {
                try
                {
                    var waited = 0;
                    while (Volatile.Read(ref _refCount) == 0)
                    {
                        if (Pending == 0)
                            break;
                        await Task.Delay(200).ConfigureAwait(false);
                        waited += 200;
                        if (waited >= 5000)
                            break;
                    }

                    if (Volatile.Read(ref _refCount) == 0)
                        Dispose();
                }
                finally
                {
                    Interlocked.Exchange(ref _disposeScheduled, 0);
                }
            });
        }

        public void EnqueueProcess(Func<CancellationToken, Task> work, TaskPriority priority = TaskPriority.Normal)
        {
            Status = LoaderStatus.Queued;
            EnsureQueue().Enqueue(async token =>
            {
                Status = LoaderStatus.Running;
                try
                {
                    await work(token);
                }
                finally
                {
                    Status = EnsureQueue().PendingCount > 0 ? LoaderStatus.Draining : LoaderStatus.Idle;
                }
            }, priority);
        }

        public void Dispose()
        {
            Status = LoaderStatus.Stopped;
            if (!_disposed)
            {
                _queue?.Dispose();
                _disposed = true;
            }
        }
    }
}

