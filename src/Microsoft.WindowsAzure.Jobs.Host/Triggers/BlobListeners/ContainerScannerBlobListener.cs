﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.WindowsAzure.StorageClient;

namespace Microsoft.WindowsAzure.Jobs
{
    // Full scan a container 
    // Uses a naive full-scanning algorithm. Easy, but very inefficient and does not scale to large containers.
    // But it is very deterministic. 
    internal class ContainerScannerBlobListener : IBlobListener
    {
        // Containers to listen to, and timestamp of last poll
        // Use parallel arrays instead of dicts to allow update during enumeration
        CloudBlobContainer[] _containers;
        DateTime[] _lastUpdateTimes;

        public ContainerScannerBlobListener(IEnumerable<CloudBlobContainer> containers)
        {
            DateTime start = new DateTime(1900, 1, 1); // old 

            _containers = containers.ToArray();

            _lastUpdateTimes = new DateTime[_containers.Length];
            for (int i = 0; i < _lastUpdateTimes.Length; i++)
            {
                _lastUpdateTimes[i] = start;
            }
        }

        // Does one iteration and then returns.
        // Poll containers, invoke callback for any new ones added.
        // $$$ Switch to queuing instead of just invoking callback
        public void Poll(Action<CloudBlob> callback, CancellationToken cancel)
        {
            for (int i = 0; !cancel.IsCancellationRequested && i < _containers.Length; i++)
            {
                var time = _lastUpdateTimes[i];

                var newBlobs = PollNewBlobs(_containers[i], time, cancel, ref _lastUpdateTimes[i]);

                foreach (var blob in newBlobs)
                {
                    callback(blob);
                }
            }
        }

        // lastScanTime - updated to the latest time in the container. Never call DateTime.Now because we need to avoid clock schewing problems. 
        public static IEnumerable<CloudBlob> PollNewBlobs(CloudBlobContainer container, DateTime timestamp, CancellationToken cancel, ref DateTime lastScanTime)
        {
            List<CloudBlob> blobs = new List<CloudBlob>();

            try
            {
                container.CreateIfNotExist();
            }
            catch (StorageException)
            {
                // Can happen if container was deleted (or being deleted). Just ignore and return empty collection.
                // If it's deleted, there's nothing to listen for.
                return blobs;
            }

            var opt = new BlobRequestOptions();
            opt.UseFlatBlobListing = true;
            foreach (var blobItem in container.ListBlobs(opt))
            {
                if (cancel.IsCancellationRequested)
                {
                    return new CloudBlob[0];
                }

                CloudBlob b = container.GetBlobReference(blobItem.Uri.ToString());

                try
                {
                    b.FetchAttributes();
                }
                catch
                {
                    // Blob was likely deleted.
                    continue;
                }

                var attrs = b.Attributes;

                var props = b.Properties;
                var time = props.LastModifiedUtc.ToLocalTime();

                if (time > lastScanTime)
                {
                    lastScanTime = time;
                }

                if (time > timestamp)
                {
                    blobs.Add(b);
                }
            }

            return blobs;
        }
    }
}