using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Zink.Models;

namespace Zink.Services.Recording
{
    public static class AudioDeviceService
    {
        public static async Task<IReadOnlyList<RecorderDeviceItem>> GetRenderDevicesAsync()
        {
            var selector = MediaDevice.GetAudioRenderSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);

            return devices
                .Select(d => new RecorderDeviceItem
                {
                    Id = d.Id,
                    Name = string.IsNullOrWhiteSpace(d.Name) ? "Unknown render device" : d.Name
                })
                .ToList();
        }

        public static async Task<IReadOnlyList<RecorderDeviceItem>> GetCaptureDevicesAsync()
        {
            var selector = MediaDevice.GetAudioCaptureSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);

            return devices
                .Select(d => new RecorderDeviceItem
                {
                    Id = d.Id,
                    Name = string.IsNullOrWhiteSpace(d.Name) ? "Unknown microphone" : d.Name
                })
                .ToList();
        }
    }
}