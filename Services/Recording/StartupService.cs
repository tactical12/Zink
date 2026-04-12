using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace Zink.Services.Recording
{
    public static class StartupService
    {
        private const string StartupTaskId = "ZinkRecorderStartup";

        public static async Task<bool> SetStartupEnabledAsync(bool enabled)
        {
            var task = await StartupTask.GetAsync(StartupTaskId);

            if (enabled)
            {
                if (task.State == StartupTaskState.Disabled)
                {
                    var newState = await task.RequestEnableAsync();
                    return newState == StartupTaskState.Enabled ||
                           newState == StartupTaskState.EnabledByPolicy;
                }

                return task.State == StartupTaskState.Enabled ||
                       task.State == StartupTaskState.EnabledByPolicy;
            }

            if (task.State == StartupTaskState.Enabled ||
                task.State == StartupTaskState.EnabledByPolicy)
            {
                task.Disable();
            }

            return false;
        }
    }
}