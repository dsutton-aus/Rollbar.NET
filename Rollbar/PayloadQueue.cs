﻿[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("UnitTest.Rollbar")]

namespace Rollbar
{

    using Rollbar.Diagnostics;
    using Rollbar.DTOs;
    using System;
    using System.Collections.Generic;
    using System.Text;

    internal class PayloadQueue
    {
        private readonly object _syncLock = null;
        private readonly Queue<Payload> _queue = null;
        private readonly RollbarLogger _logger = null;

        private PayloadQueue()
        {
        }

        public PayloadQueue(RollbarLogger logger)
        {
            Assumption.AssertNotNull(logger, nameof(logger));

            this._logger = logger;
            this._syncLock = new object();
            this._queue = new Queue<Payload>();
        }

        public DateTimeOffset NextDequeueTime { get; internal set; }

        public RollbarLogger Logger
        {
            get { return this._logger; }
        }

        public void Enqueue(Payload payload)
        {
            Assumption.AssertNotNull(payload, nameof(payload));

            lock (this._syncLock)
            {
                this._queue.Enqueue(payload);
            }
        }

        public Payload Peek()
        {
            lock(this._syncLock)
            {
                Payload result = null;

                if (this._queue.Count > 0)
                {
                    result = this._queue.Peek();
                }

                return result;
            }
        }

        public Payload Dequeue()
        {
            lock (this._syncLock)
            {
                Payload result = null;

                if (this._queue.Count > 0)
                {
                    result = this._queue.Dequeue();

                    TimeSpan delta = TimeSpan.FromTicks(
                        TimeSpan.FromMinutes(1).Ticks / this.Logger.Config.MaxReportsPerMinute
                        );
                    this.NextDequeueTime = DateTimeOffset.Now.Add(delta);
                }

                return result;
            }
        }
    }
}
