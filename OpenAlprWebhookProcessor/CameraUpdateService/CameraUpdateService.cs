﻿using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAlprWebhookProcessor.Cameras;
using OpenAlprWebhookProcessor.Data;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAlprWebhookProcessor.CameraUpdateService
{
    public class CameraUpdateService : IHostedService
    {
        private readonly IBackgroundJobClient _backgroundJobClient;

        private readonly CancellationTokenSource _cancellationTokenSource;

        private readonly IServiceProvider _serviceProvider;

        private readonly ILogger _logger;

        public CameraUpdateService(
            IServiceProvider serviceProvider,
            ILogger<CameraUpdateService> logger,
            IBackgroundJobClient backgroundJobClient)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _cancellationTokenSource = new CancellationTokenSource();
            _backgroundJobClient = backgroundJobClient;
        }

        public async Task ForceSunriseSunsetAsync()
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var processorContext = scope.ServiceProvider.GetRequiredService<ProcessorContext>();

                var agent = await processorContext.Agents.FirstOrDefaultAsync();

                var camerasToUpdate = await processorContext.Cameras
                    .Where(x => x.UpdateDayNightModeEnabled)
                    .ToListAsync();

                foreach (var camera in camerasToUpdate)
                {
                    _backgroundJobClient.Enqueue(() => ProcessSunriseSunsetJobAsync(
                        camera.Id,
                        CameraScheduling.IsSunUp(
                            agent.Latitude,
                            agent.Longitude) ? SunriseSunset.Sunrise : SunriseSunset.Sunset,
                        false));
                }
            }
        }

        public async Task DeleteSunriseSunsetAsync(Guid cameraId)
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var processorContext = scope.ServiceProvider.GetRequiredService<ProcessorContext>();

                var cameraToUpdate = await processorContext.Cameras
                    .Where(x => x.Id == cameraId)
                    .FirstOrDefaultAsync();

                if (cameraToUpdate == null)
                {
                    throw new ArgumentException("Camera not found");
                }

                if (!string.IsNullOrWhiteSpace(cameraToUpdate.NextDayNightScheduleId))
                {
                    _backgroundJobClient.Delete(cameraToUpdate.NextDayNightScheduleId);

                    cameraToUpdate.NextDayNightScheduleId = string.Empty;
                    await processorContext.SaveChangesAsync();
                }
            }
        }

        public async Task ProcessSunriseSunsetJobAsync(
            Guid cameraId,
            SunriseSunset sunriseSunset,
            bool scheduleNextJob)
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var processorContext = scope.ServiceProvider.GetRequiredService<ProcessorContext>();

                _logger.LogInformation($"setting {sunriseSunset} for {cameraId}");

                try
                {
                    var cameraToUpdate = await processorContext.Cameras.FirstOrDefaultAsync(x => x.Id == cameraId);

                    if (cameraToUpdate == null)
                    {
                        _logger.LogError($"Unable to find camera with OpenAlprId: {cameraId}, check your configuration.");
                    }

                    var camera = CameraFactory.Create(cameraToUpdate.Manufacturer, cameraToUpdate);
                    await camera.TriggerDayNightModeAsync(
                        sunriseSunset,
                        _cancellationTokenSource.Token);

                    if (scheduleNextJob)
                    {
                        _logger.LogInformation($"Scheduling next job for {cameraId}");

                        var agent = await processorContext.Agents.FirstOrDefaultAsync();

                        CameraScheduling.ScheduleDayNightTask(
                            this,
                            _backgroundJobClient,
                            agent,
                            cameraToUpdate);

                        await processorContext.SaveChangesAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message);
                }
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Task.Run(async () =>
            {
                await ForceSunriseSunsetAsync();

                await CameraScheduling.ScheduleDayNightTasksAsync(
                    this,
                    _serviceProvider,
                    _backgroundJobClient);

                await ForceClearOverlaysAsync();
            }, cancellationToken);

            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _cancellationTokenSource.Cancel();

            await ForceClearOverlaysAsync();
        }

        public async Task ScheduleDayNightTaskAsync()
        {
            await CameraScheduling.ScheduleDayNightTasksAsync(
                this,
                _serviceProvider,
                _backgroundJobClient);
        }

        public void EnqueueDayNight(
            Guid cameraId,
            SunriseSunset sunriseSunset)
        {
            CameraScheduling.ExecuteSingleDayNightTask(
                sunriseSunset,
                cameraId,
                this,
                _backgroundJobClient);
        }

        public void ScheduleOverlayRequest(CameraUpdateRequest cameraUpdateRequest)
        {
            _backgroundJobClient.Enqueue(() => ProcessJobAsync(cameraUpdateRequest));
        }

        public async Task ProcessJobAsync(CameraUpdateRequest cameraUpdateRequest)
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var processorContext = scope.ServiceProvider.GetRequiredService<ProcessorContext>();

                _logger.LogInformation("processing job for plate: " + cameraUpdateRequest.LicensePlate);

                try
                {
                    var cameraToUpdate = await processorContext.Cameras.FirstOrDefaultAsync(x => x.Id == cameraUpdateRequest.Id);

                    if (cameraToUpdate == null)
                    {
                        _logger.LogError($"Unable to find camera with OpenAlprId: {cameraUpdateRequest.Id}, check your configuration.");
                        throw new ArgumentException($"unknown camera Id: {cameraUpdateRequest.Id}");
                    }

                    if (!string.IsNullOrWhiteSpace(cameraToUpdate.NextClearOverlayScheduleId))
                    {
                        _logger.LogInformation($"cancelling redundant clear overlay job: {cameraToUpdate.NextClearOverlayScheduleId}");
                        _backgroundJobClient.Delete(cameraToUpdate.NextClearOverlayScheduleId);
                    }

                    var camera = CameraFactory.Create(cameraToUpdate.Manufacturer, cameraToUpdate);

                    await camera.SetCameraTextAsync(
                        cameraUpdateRequest,
                        _cancellationTokenSource.Token);

                    cameraToUpdate.NextClearOverlayScheduleId = _backgroundJobClient.Schedule(
                       () => ClearExpiredOverlayAsync(cameraUpdateRequest.Id),
                       TimeSpan.FromSeconds(5));

                    if (!cameraUpdateRequest.IsTest
                        && !cameraUpdateRequest.IsPreviewGroup
                        && !cameraUpdateRequest.IsSinglePlate)
                    {
                        cameraToUpdate.PlatesSeen++;
                        cameraToUpdate.LatestProcessedPlateUuid = cameraUpdateRequest.LicensePlateImageUuid;
                    }

                    await processorContext.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message);
                }
            }
        }

        public async Task ClearExpiredOverlayAsync(Guid cameraId)
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var processorContext = scope.ServiceProvider.GetRequiredService<ProcessorContext>();

                var cameraToUpdate = await processorContext.Cameras.FirstOrDefaultAsync(x => x.Id == cameraId);

                _logger.LogInformation("clearing expired overlay for: " + cameraToUpdate.OpenAlprCameraId);

                var camera = CameraFactory.Create(cameraToUpdate.Manufacturer, cameraToUpdate);

                await camera.ClearCameraTextAsync(
                    _cancellationTokenSource.Token);

                cameraToUpdate.NextClearOverlayScheduleId = string.Empty;

                await processorContext.SaveChangesAsync();
            }
        }

        private async Task ForceClearOverlaysAsync()
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var processorContext = scope.ServiceProvider.GetRequiredService<ProcessorContext>();

                foreach (var cameraToUpdate in await processorContext.Cameras.ToListAsync())
                {
                    _logger.LogInformation("force clearing overlay for: " + cameraToUpdate.OpenAlprCameraId);

                    var camera = CameraFactory.Create(cameraToUpdate.Manufacturer, cameraToUpdate);

                    await camera.ClearCameraTextAsync(
                        _cancellationTokenSource.Token);

                    cameraToUpdate.NextClearOverlayScheduleId = string.Empty;
                }

                await processorContext.SaveChangesAsync();
            }
        }
    }
}
