﻿[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("UnitTest.Rollbar")]

namespace Rollbar
{
    using Rollbar.Diagnostics;
    using Rollbar.DTOs;
    using Rollbar.Serialization.Json;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Threading;

    /// <summary>
    /// RollbarQueueController singleton.
    /// It keeps track of payload queues of every instance of RollbarLogger.
    /// It is also responsible for async processing of queues on its own worker thread 
    /// (including retries as necessary).
    /// It makes sure that Rollbar access token rate limits handled properly.
    /// </summary>
    public sealed class RollbarQueueController
    {
        #region singleton implementation

        /// <summary>
        /// Gets the instance.
        /// </summary>
        /// <value>
        /// The instance.
        /// </value>
        public static RollbarQueueController Instance
        {
            get
            {
                return NestedSingleInstance.Instance;
            }
        }

        /// <summary>
        /// Prevents a default instance of the <see cref="RollbarQueueController"/> class from being created.
        /// </summary>
        private RollbarQueueController()
        {
            this._rollbarCommThread = new Thread(this.KeepProcessingAllQueues)
            {
                IsBackground = true,
                Name = "Rollbar Communication Thread"
            };

            this._rollbarCommThread.Start();
        }

        private sealed class NestedSingleInstance
        {
            static NestedSingleInstance()
            {
            }

            internal static readonly RollbarQueueController Instance = 
                new RollbarQueueController();
        }

        #endregion singleton implementation

        /// <summary>
        /// Occurs after a Rollbar internal event happens.
        /// </summary>
        public event EventHandler<RollbarEventArgs> InternalEvent;

        /// <summary>
        /// Registers the specified queue.
        /// </summary>
        /// <param name="queue">The queue.</param>
        internal void Register(PayloadQueue queue)
        {
            lock (this._syncLock)
            {
                Assumption.AssertTrue(!this._allQueues.Contains(queue), nameof(queue));

                this._allQueues.Add(queue);
                this.IndexByToken(queue);
                queue.Logger.Config.Reconfigured += Config_Reconfigured;
                Debug.WriteLine(this.GetType().Name + ": Registered a queue. Total queues count: " + this._allQueues.Count + ".");
            }
        }

        /// <summary>
        /// Unregisters the specified queue.
        /// </summary>
        /// <param name="queue">The queue.</param>
        internal void Unregister(PayloadQueue queue)
        {
            lock (this._syncLock)
            {
                Assumption.AssertTrue(!queue.Logger.IsSingleton, nameof(queue.Logger.IsSingleton));
                Assumption.AssertTrue(this._allQueues.Contains(queue), nameof(queue));

                this.DropIndexByToken(queue);
                this._allQueues.Remove(queue);
                queue.Logger.Config.Reconfigured -= Config_Reconfigured;
                Debug.WriteLine(this.GetType().Name + ": Unregistered a queue. Total queues count: " + this._allQueues.Count + ".");
            }
        }

        internal int GetQueuesCount(string accessToken = null)
        {
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                if (this._queuesByAccessToken.TryGetValue(accessToken, out AccessTokenQueuesMetadata metadata))
                {
                    return metadata.Queues.Count;
                }
                return 0;
            }

            int result = 0;
            foreach(var md in this._queuesByAccessToken.Values)
            {
                result += md.Queues.Count;
            }
            return result;
        }

        private readonly object _syncLock = new object();

        private readonly Thread _rollbarCommThread = null;

        private readonly HashSet<PayloadQueue> _allQueues =
            new HashSet<PayloadQueue>();

        private readonly Dictionary<string, AccessTokenQueuesMetadata> _queuesByAccessToken = 
            new Dictionary<string, AccessTokenQueuesMetadata>();

        private void KeepProcessingAllQueues()
        {
            TimeSpan sleepInterval = TimeSpan.FromMilliseconds(250);

            while(true)
            {
                try
                {
                    lock(this._syncLock)
                    {
                        ProcessAllQueuesOnce();
                    }

                    Thread.Sleep(sleepInterval);
                }
#pragma warning disable CS0168 // Variable is declared but never used
                catch (System.Exception ex)
#pragma warning restore CS0168 // Variable is declared but never used
                {
                    //TODO: do we want to direct the exception 
                    //      to some kind of Rollbar notifier maintenance "access token"?
                }
            }
        }

        private void ProcessAllQueuesOnce()
        {
            foreach(var token in this._queuesByAccessToken.Keys)
            {
                if (this._queuesByAccessToken[token].NextTimeTokenUsage.HasValue
                    && this._queuesByAccessToken[token].NextTimeTokenUsage.Value > DateTimeOffset.Now
                    )
                {
                    //skip this token's queue for now, until past NextTimeTokenUsage:
                    continue;
                }
                ProcessQueues(this._queuesByAccessToken[token]);
            }
        }

        private void ProcessQueues(AccessTokenQueuesMetadata tokenMetadata)
        {
            foreach (var queue in tokenMetadata.Queues)
            {
                if (DateTimeOffset.Now >= queue.NextDequeueTime)
                {
                    Payload payload = queue.Peek();
                    if (payload == null)
                    {
                        continue;
                    }

                    var response = Process(payload, queue.Logger.Config);
                    if (response == null)
                    {
                        continue;
                    }

                    switch (response.Error)
                    {
                        case (int)RollbarApiErrorEventArgs.RollbarError.None:
                            queue.Dequeue();
                            tokenMetadata.ResetTokenUsageDelay();
                            break;
                        case (int)RollbarApiErrorEventArgs.RollbarError.TooManyRequests:
                            tokenMetadata.IncrementTokenUsageDelay();
                            OnRollbarEvent(
                                new RollbarApiErrorEventArgs(queue.Logger.Config, payload, response)
                                );
                            return;
                        default:
                            OnRollbarEvent(
                                new RollbarApiErrorEventArgs(queue.Logger.Config, payload, response)
                                );
                            break;
                    }

                }
            }
        }

        private RollbarResponse Process(Payload payload, RollbarConfig config)
        {
            var client = new RollbarClient(config);

            IEnumerable<string> safeScrubFields = config.ScrubFields;

            RollbarResponse response = null;
            int retries = 3;
            while (retries > 0)
            {
                try
                {
                    response = client.PostAsJson(payload, safeScrubFields);
                }
                catch (WebException ex)
                {
                    retries--;
                    this.OnRollbarEvent(
                        new CommunicationErrorEventArgs(config, payload, ex, retries)
                        );
                    continue;
                }
                catch (ArgumentNullException ex)
                {
                    retries = 0;
                    this.OnRollbarEvent(
                        new CommunicationErrorEventArgs(config, payload, ex, retries)
                        );
                    continue;
                }
                catch (System.Exception ex)
                {
                    retries = 0;
                    this.OnRollbarEvent(
                        new CommunicationErrorEventArgs(config, payload, ex, retries)
                        );
                    continue;
                }
                retries = 0;
            }

            if (response != null)
            {
                this.OnRollbarEvent(
                    new CommunicationEventArgs(config, payload, response)
                    );
            }

            return response;
        }

        private void Config_Reconfigured(object sender, EventArgs e)
        {
            lock (this._syncLock)
            {
                RollbarConfig config = (RollbarConfig)sender;
                Assumption.AssertNotNull(config, nameof(config));

                PayloadQueue queue = config.Logger.Queue;
                Assumption.AssertNotNull(queue, nameof(queue));

                //refresh indexing:
                this.DropIndexByToken(queue);
                this.IndexByToken(queue);
                Debug.WriteLine(this.GetType().Name + ": Re-indexed a reconfigured queue. Total queues count: " + this._allQueues.Count + ".");
            }
        }

        private void IndexByToken(PayloadQueue queue)
        {
            string queueToken = queue.Logger.Config.AccessToken;
            if (queueToken == null)
            {
                //this is a valid case for the RollbarLogger singleton instance,
                //when the instance is created but not configured yet...
                return;
            }

            if (!this._queuesByAccessToken.TryGetValue(queueToken, out AccessTokenQueuesMetadata tokenMetadata))
            {
                tokenMetadata = new AccessTokenQueuesMetadata(queueToken);
                this._queuesByAccessToken.Add(queueToken, tokenMetadata);
            }
            tokenMetadata.Queues.Add(queue);
        }

        private void DropIndexByToken(PayloadQueue queue)
        {
            foreach (var tokenMetadata in this._queuesByAccessToken.Values)
            {
                if (tokenMetadata.Queues.Contains(queue))
                {
                    tokenMetadata.Queues.Remove(queue);
                    break;
                }
            }
        }

        private void OnRollbarEvent(RollbarEventArgs e)
        {
            EventHandler<RollbarEventArgs> handler = InternalEvent;

            if (handler != null)
            {
                handler(this, e);
            }
        }

    }
}
