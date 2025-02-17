﻿using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAlprWebhookProcessor.Data;
using OpenAlprWebhookProcessor.ImageRelay;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAlprWebhookProcessor.WebhookProcessor
{
    public class ImageRetrieverService : IHostedService
    {
        private readonly BlockingCollection<string> _imageRequestsToProcess = new BlockingCollection<string>();

        private readonly HashSet<string> _imageRequestsToProcessList = new();

        private readonly object _imageRequestsToProcessGate = new();

        private readonly BlockingCollection<string> _imageCompressionRequestsToProcess = new();

        private readonly HashSet<string> _imageCompressionRequestsToProcessList = new();

        private readonly object _imageCompressionRequestsToGate = new();

        private readonly CancellationTokenSource _cancellationTokenSource;

        private readonly IServiceProvider _serviceProvider;

        public ImageRetrieverService(IServiceProvider serviceProvider)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _serviceProvider = serviceProvider;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Task.Run(async () =>
                await ProcessImageRequestsAsync(),
                cancellationToken);

            // Task.Run(async () =>
            //     await ProcessImageCompressionRequestsAsync(),
            //     cancellationToken);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _imageCompressionRequestsToProcess.CompleteAdding();
            _imageRequestsToProcess.CompleteAdding();
            _cancellationTokenSource.Cancel();

            return Task.CompletedTask;
        }

        public bool TryAddJob(string openAlprImageId)
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<ImageRetrieverService>>();
                //logger.LogInformation("adding job for image: {imageId}", openAlprImageId);

                lock (_imageRequestsToProcessGate)
                {
                    if (_imageRequestsToProcessList.Contains(openAlprImageId))
                    {
                        logger.LogInformation("image is already queued for processing: {imageId}", openAlprImageId);
                        return false;
                    }
                    else
                    {
                        if (!_imageRequestsToProcess.TryAdd(openAlprImageId))
                        {
                            logger.LogError("Unable to queue image for processing: {imageId}", openAlprImageId);
                            return false;
                        }

                        _imageRequestsToProcessList.Add(openAlprImageId);
                        return true;
                    }
                }
            }
        }

        public void AddImageCompressionJob()
        {
            _imageCompressionRequestsToProcess.Add("allImages");
        }

        private async Task ProcessImageRequestsAsync()
        {
            foreach (var job in _imageRequestsToProcess.GetConsumingEnumerable(_cancellationTokenSource.Token))
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var logger = scope.ServiceProvider.GetRequiredService<ILogger<ImageRetrieverService>>();
                    //logger.LogInformation("{numberOfRequests} images queued for processing", _imageRequestsToProcess.Count);

                    var processorContext = scope.ServiceProvider.GetRequiredService<ProcessorContext>();

                    var plateGroups = await processorContext.PlateGroups
                        .Include(x => x.PlateImage)
                        //.Include(x => x.VehicleImage)
                        .Where(x => x.OpenAlprUuid == job)
                        .ToListAsync(_cancellationTokenSource.Token);

                    var isImageCompressionEnabled = await processorContext.Agents
                        .AsNoTracking()
                        .Select(x => x.IsImageCompressionEnabled)
                        .FirstOrDefaultAsync();

                    foreach (var plateGroup in plateGroups)
                    {
                        // if (plateGroup == null)
                        // {
                        //     logger.LogError("Unable to find openalpr group id: {groupId}", job);
                        //     continue;
                        // }

                        // try
                        // {
                            var image = await GetImageHandler.GetImageFromAgentAsync(
                                processorContext,
                                job,
                                _cancellationTokenSource.Token);

                            var cropImage = await GetImageHandler.GetCropImageFromAgentAsync(
                                processorContext,
                                job + "?" + plateGroup.PlateCoordinates,
                                _cancellationTokenSource.Token);

                            plateGroup.PlateImage = new PlateImage()
                            {
                                Jpeg = cropImage,
                                IsCompressed = isImageCompressionEnabled,
                            };

                            var currentDay = DateTime.Now.Day.ToString("00");
                            var currentMonth = DateTime.Now.Month.ToString("00");
                            var currentYear = DateTime.Now.Year.ToString();

                            var PlateImagePath = GlobalSettings.Setup.platePath + currentYear + "/" + currentMonth + "/" + currentDay + "/";

                            System.IO.Directory.CreateDirectory(PlateImagePath);
                            System.IO.File.WriteAllBytes(PlateImagePath + job + ".jpg", plateGroup.PlateImage.Jpeg);

                            plateGroup.VehicleImage = new VehicleImage()
                            {
                                Jpeg = image,
                                IsCompressed = isImageCompressionEnabled,
                            };

                            var VehicleImagePath = GlobalSettings.Setup.vehiclePath + currentYear + "/" + currentMonth + "/" + currentDay + "/";

                            System.IO.Directory.CreateDirectory(VehicleImagePath);
                            System.IO.File.WriteAllBytes(VehicleImagePath + job + ".jpg", plateGroup.VehicleImage.Jpeg);

                        // }
                        // catch (Exception ex)
                        // {
                        //     logger.LogError(ex, "Unable to retrieve image from Agent: {imageId}", job);
                        // }

                        // plateGroup.AgentImageScrapeOccurredOn = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        // await processorContext.SaveChangesAsync(_cancellationTokenSource.Token);

                        // lock (_imageRequestsToProcessGate)
                        // {
                        //     _imageRequestsToProcessList.Remove(job);
                        // }
                    }

                    //logger.LogInformation("finished job for image: {imageId}", job);
                }
            }
        }

        // private async Task ProcessImageCompressionRequestsAsync()
        // {
        //     foreach (var job in _imageCompressionRequestsToProcess.GetConsumingEnumerable(_cancellationTokenSource.Token))
        //     {
        //         using (var scope = _serviceProvider.CreateScope())
        //         {
        //             bool keepPaging = true;
        //             long lastReceivedOnEpoch = 0;

        //             while (keepPaging)
        //             {
        //                 var processorContext = scope.ServiceProvider.GetRequiredService<ProcessorContext>();
        //                 var logger = scope.ServiceProvider.GetRequiredService<ILogger<ImageRetrieverService>>();

        //                 var isImageCompressionEnabled = await processorContext.Agents
        //                     .AsNoTracking()
        //                     .Select(x => x.IsImageCompressionEnabled)
        //                     .FirstOrDefaultAsync();

        //                 if (!isImageCompressionEnabled)
        //                 {
        //                     logger.LogWarning("Image compression disabled, check agent settings.");
        //                     break;
        //                 }

        //                 var plateGroups = await processorContext.PlateGroups
        //                     .Include(x => x.PlateImage)
        //                     //.Include(x => x.VehicleImage)
        //                     .OrderBy(x => x.ReceivedOnEpoch)
        //                     .Where(x => x.ReceivedOnEpoch > lastReceivedOnEpoch)
        //                     .Where(x => !x.PlateImage.IsCompressed || !x.VehicleImage.IsCompressed)
        //                     .Where(x => x.PlateImage.Jpeg.Length > 0 || x.VehicleImage.Jpeg.Length > 0)
        //                     .Take(25)
        //                     .ToListAsync(_cancellationTokenSource.Token);

        //                 if (!plateGroups.Any())
        //                 {
        //                     keepPaging = false;
        //                 }
        //                 else
        //                 {
        //                     lastReceivedOnEpoch = plateGroups.First().ReceivedOnEpoch;
        //                 }

        //                 logger.LogInformation("Searcing for images newer than {epoch}: {numberOfRequests} images queued for compression", lastReceivedOnEpoch, plateGroups.Count);

        //                 foreach (var plateGroup in plateGroups)
        //                 {
        //                     // if (!plateGroup.VehicleImage.IsCompressed && plateGroup.VehicleImage.Jpeg != null)
        //                     // {
        //                     //     plateGroup.VehicleImage.Jpeg = GetImageHandler.CompressImage(plateGroup.VehicleImage.Jpeg);
        //                     //     plateGroup.VehicleImage.IsCompressed = true;
        //                     // }

        //                     if (!plateGroup.PlateImage.IsCompressed && plateGroup.PlateImage.Jpeg != null)
        //                     {
        //                         plateGroup.PlateImage.Jpeg = GetImageHandler.CompressImage(plateGroup.PlateImage.Jpeg);
        //                         plateGroup.PlateImage.IsCompressed = true;
        //                     }
        //                 }

        //                 await processorContext.SaveChangesAsync(_cancellationTokenSource.Token);
        //             }
        //         }
        //     }
        // }
    }
}