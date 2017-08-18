﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.EventHubs.Tests.Client
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Xunit;

    public class DataBatchTests : ClientTestBase
    {
        /// <summary>
        /// Utilizes EventDataBatch to send messages as the messages are batched up to max batch size.
        /// </summary>
        [Fact]
        [DisplayTestMethodName]
        async Task BatchSender()
        {
            await SendWithEventDataBatch();
        }

        /// <summary>
        /// Utilizes EventDataBatch to send messages as the messages are batched up to max batch size.
        /// This unit test sends with partition key.
        /// </summary>
        [Fact]
        [DisplayTestMethodName]
        async Task BatchSenderWithPartitionKey()
        {
            await SendWithEventDataBatch(Guid.NewGuid().ToString());
        }

        /// <summary>
        /// Client should not allow to send a batch with partition key on a partition sender.
        /// </summary>
        [Fact]
        [DisplayTestMethodName]
        async Task SendingPartitionKeyBatchOnPartitionSenderShouldFail()
        {
            var partitionSender = this.EventHubClient.CreatePartitionSender("0");
            var batcher = this.EventHubClient.CreateBatch("this is the partition key");

            // GetRuntimeInformationAsync on a nonexistent entity.
            await Assert.ThrowsAsync<ArgumentException>(async () =>
            {
                TestUtility.Log("Attempting to send a partition-key batch on partition sender. This should fail.");
                await partitionSender.SendAsync(batcher);
                throw new InvalidOperationException("SendAsync call should have failed");
            });
        }

        protected async Task SendWithEventDataBatch(string partitionKey = null)
        {
            const int MinimumNumberOfMessagesToSend = 1000;

            var receivers = new List<PartitionReceiver>();

            // Create partition receivers starting from the end of the stream.
            TestUtility.Log("Discovering end of stream on each partition.");
            foreach (var partitionId in this.PartitionIds)
            {
                var lastEvent = await this.EventHubClient.GetPartitionRuntimeInformationAsync(partitionId);
                receivers.Add(this.EventHubClient.CreateReceiver(PartitionReceiver.DefaultConsumerGroupName, partitionId, lastEvent.LastEnqueuedOffset));
            }

            try
            {
                // Start receicing messages now.
                var receiverTasks = new List<Task<List<EventData>>>();
                foreach (var receiver in receivers)
                {
                    receiverTasks.Add(ReceiveAllMessages(receiver));
                }

                // Create initial batcher.
                EventDataBatch batcher = this.EventHubClient.CreateBatch(partitionKey);

                // We will send a thousand messages where each message is 1K.
                var totalSent = 0;
                var rnd = new Random();
                TestUtility.Log($"Starting to send.");
                do
                {
                    // Send random body size.
                    var ed = new EventData(new byte[rnd.Next(0, 1024)]);
                    if (!batcher.TryAdd(ed))
                    {
                        // Time to send the batch.
                        if (partitionKey != null)
                        {
                            await this.EventHubClient.SendAsync(batcher, partitionKey);
                        }
                        else
                        {
                            await this.EventHubClient.SendAsync(batcher);
                        }

                        totalSent += batcher.Count;
                        TestUtility.Log($"Sent {batcher.Count} messages in the batch.");

                        // Create new batcher.
                        batcher = this.EventHubClient.CreateBatch(partitionKey);
                    }
                } while (totalSent < MinimumNumberOfMessagesToSend);

                // Send the rest of the batch if any.
                if (batcher.Count > 0)
                {
                    await this.EventHubClient.SendAsync(batcher);
                    totalSent += batcher.Count;
                    TestUtility.Log($"Sent {batcher.Count} messages in the batch.");
                }

                TestUtility.Log($"{totalSent} messages sent in total.");

                var pReceived = await Task.WhenAll(receiverTasks);
                var totalReceived = pReceived.Sum(p => p.Count);
                TestUtility.Log($"{totalReceived} messages received in total.");

                // All messages received?
                Assert.True(totalReceived == totalSent, $"Failed receive {totalSent}, but received {totalReceived} messages.");

                // If partition key is set then we expect all messages from the same partition.
                if (partitionKey != null)
                {
                    Assert.True(pReceived.Count(p => p.Count > 0) == 1, "Received messsages from multiple partitions.");
                }

                // Validate that all messages landed on a single partition.
                var targetPartition = pReceived.Single(p => p.Count > 0);

                // Validate partition key is delivered on all messages.
                Assert.True(!targetPartition.Any(p => p.SystemProperties.PartitionKey != partitionKey), "Identified at least one event with a different partition key value.");
            }
            finally
            {
                await Task.WhenAll(receivers.Select(r => r.CloseAsync()));
            }
        }
    }
}