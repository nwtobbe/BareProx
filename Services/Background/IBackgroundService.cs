using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BareProx.Services.Background
{
    /// <summary>
    /// A simple background task queue for enqueuing fire-and-forget work items.
    /// </summary>
    public interface IBackgroundServiceQueue
    {
        /// <summary>
        /// Queue a background work item.
        /// </summary>
        /// <param name="workItem">The work to execute.</param>
        void QueueBackgroundWorkItem(Func<CancellationToken, Task> workItem);

        /// <summary>
        /// Dequeue the next work item. Waits until one is available or cancellation is requested.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The dequeued work item.</returns>
        Task<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Default implementation of <see cref="IBackgroundServiceQueue"/> using a Channel.
    /// </summary>
    public class BackgroundServiceQueue : IBackgroundServiceQueue
    {
        private readonly Channel<Func<CancellationToken, Task>> _queue;

        public BackgroundServiceQueue()
        {
            // Unbounded channel to hold work items
            _queue = Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false
            });
        }

        public void QueueBackgroundWorkItem(Func<CancellationToken, Task> workItem)
        {
            if (workItem == null)
                throw new ArgumentNullException(nameof(workItem));

            if (!_queue.Writer.TryWrite(workItem))
                throw new InvalidOperationException("Unable to queue background work item.");
        }

        public async Task<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken)
        {
            var workItem = await _queue.Reader.ReadAsync(cancellationToken);
            return workItem;
        }
    }

    /// <summary>
    /// Hosted service that processes queued background work items.
    /// </summary>
    public class QueuedBackgroundService : BackgroundService
    {
        private readonly IBackgroundServiceQueue _taskQueue;
        private readonly ILogger<QueuedBackgroundService> _logger;

        public QueuedBackgroundService(IBackgroundServiceQueue taskQueue, ILogger<QueuedBackgroundService> logger)
        {
            _taskQueue = taskQueue;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Queued Background Service is starting.");

            // Allow up to 4 concurrent background jobs
            var concurrencyLimiter = new SemaphoreSlim(4, 4);
            var runningTasks = new List<Task>();

            while (!stoppingToken.IsCancellationRequested)
            {
                Func<CancellationToken, Task> workItem;
                try
                {
                    workItem = await _taskQueue.DequeueAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Wait for a free “slot” before starting the next job
                await concurrencyLimiter.WaitAsync(stoppingToken);

                var task = Task.Run(async () =>
                {
                    try
                    {
                        await workItem(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error occurred executing background work item.");
                    }
                    finally
                    {
                        concurrencyLimiter.Release();
                    }
                }, stoppingToken);

                runningTasks.Add(task);

                // Clean up completed tasks so the list doesn’t grow indefinitely
                runningTasks.RemoveAll(t => t.IsCompleted);
            }

            // Once we’ve asked to stop, wait for any in-flight workItems to finish
            try
            {
                await Task.WhenAll(runningTasks);
            }
            catch
            {
                // All exceptions were already logged inside the Task.Run above.
            }

            _logger.LogInformation("Queued Background Service is stopping.");
        }

    }
}
