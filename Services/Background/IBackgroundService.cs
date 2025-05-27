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
                SingleReader = true,
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

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var workItem = await _taskQueue.DequeueAsync(stoppingToken);
                    await workItem(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred executing background work item.");
                }
            }

            _logger.LogInformation("Queued Background Service is stopping.");
        }
    }
}
