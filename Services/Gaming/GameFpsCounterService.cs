using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Zink.Services.Gaming
{
    public sealed class GameFpsCounterService : IDisposable
    {
        private const string RtssMapName = "RTSSSharedMemoryV2";
        private const uint RtssSignature = 0x52545353;
        private const uint RtssVersion2 = 0x00020000;
        private const double MaxReasonableFps = 500.0;

        private int _targetProcessId;
        private readonly Queue<(DateTimeOffset Time, double Fps)> _displaySamples = new();
        private double _currentFps;
        private int _displayFps;
        private DateTimeOffset _lastLogAt;
        private DateTimeOffset _lastMissingLogAt;
        private bool _isRunning;
        private bool _triedStartRtss;

        public bool IsRunning => _isRunning;
        public double CurrentFps => _currentFps;
        public string LogPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Zink",
            "Logs",
            "FpsCounter",
            "fps-counter.log");

        public bool Start(int processId)
        {
            Stop();

            if (processId <= 0)
            {
                WriteLog("Game FPS counter not started: no process id was selected.");
                return false;
            }

            _targetProcessId = processId;
            _currentFps = 0;
            _displayFps = 0;
            _triedStartRtss = false;
            _isRunning = true;
            WriteLog($"Starting RTSS game FPS reader for process id {processId}.");

            var fps = Poll();
            if (fps <= 0)
                WriteLog("RTSS reader started, but no FPS value is available yet. Make sure RivaTuner Statistics Server is running and its OSD/application detection is enabled for the game.");

            return true;
        }

        public double Poll()
        {
            if (!_isRunning || _targetProcessId <= 0)
                return _currentFps;

            try
            {
                using var mappedFile = MemoryMappedFile.OpenExisting(RtssMapName, MemoryMappedFileRights.Read);
                using var view = mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

                if (view.ReadUInt32(0) != RtssSignature || view.ReadUInt32(4) < RtssVersion2)
                {
                    WriteMissingLog("RTSS shared memory exists but has an unsupported signature/version.");
                    return _currentFps;
                }

                var appEntrySize = view.ReadUInt32(8);
                var appArrayOffset = view.ReadUInt32(12);
                var appArraySize = view.ReadUInt32(16);

                for (var i = 0; i < appArraySize; i++)
                {
                    var entryOffset = appArrayOffset + i * appEntrySize;
                    var processId = view.ReadUInt32(entryOffset);
                    if (processId == 0 || processId != _targetProcessId)
                        continue;

                    var fps = ReadFpsFromEntry(view, (long)entryOffset);
                    if (fps > 0)
                    {
                        _currentFps = SmoothDisplayFps(fps);
                        LogFpsSample(ReadEntryName(view, (long)entryOffset), fps);
                    }

                    return _currentFps;
                }

                WriteMissingLog($"RTSS shared memory is available, but process id {_targetProcessId} is not listed yet.");
            }
            catch (FileNotFoundException)
            {
                if (!_triedStartRtss && TryStartRtss())
                {
                    _triedStartRtss = true;
                    WriteMissingLog("RTSS was started. Waiting for RTSS shared memory and game detection.");
                }
                else
                {
                    _triedStartRtss = true;
                    WriteMissingLog("RTSS shared memory was not found and RTSS.exe is not installed in a known location. Install/start RivaTuner Statistics Server and enable application detection for the game.");
                }
            }
            catch (Exception ex)
            {
                WriteMissingLog($"RTSS FPS read failed: {ex.Message}");
            }

            return _currentFps;
        }

        public void Stop()
        {
            if (_isRunning)
                WriteLog("Stopping RTSS game FPS reader.");

            _isRunning = false;
            _targetProcessId = 0;
            _displaySamples.Clear();
            _currentFps = 0;
            _displayFps = 0;
            _triedStartRtss = false;
            _lastLogAt = DateTimeOffset.MinValue;
            _lastMissingLogAt = DateTimeOffset.MinValue;
        }

        private bool TryStartRtss()
        {
            foreach (var path in GetRtssCandidatePaths())
            {
                try
                {
                    if (!File.Exists(path))
                        continue;

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Minimized
                    });
                    WriteLog($"Started RTSS from {path}.");
                    return true;
                }
                catch (Exception ex)
                {
                    WriteLog($"Failed to start RTSS from {path}: {ex.Message}");
                }
            }

            return false;
        }

        private static string[] GetRtssCandidatePaths()
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return new[]
            {
                Path.Combine(programFiles, "RivaTuner Statistics Server", "RTSS.exe"),
                Path.Combine(programFilesX86, "RivaTuner Statistics Server", "RTSS.exe"),
                Path.Combine(programFiles, "MSI Afterburner", "RTSS.exe"),
                Path.Combine(programFilesX86, "MSI Afterburner", "RTSS.exe"),
                Path.Combine(programFiles, "MSI Afterburner", "Bundle", "OSDServer", "RTSS.exe"),
                Path.Combine(programFilesX86, "MSI Afterburner", "Bundle", "OSDServer", "RTSS.exe")
            };
        }

        public void Dispose()
        {
            Stop();
        }

        private double ReadFpsFromEntry(MemoryMappedViewAccessor view, long entryOffset)
        {
            var time0 = view.ReadUInt32(entryOffset + 268);
            var time1 = view.ReadUInt32(entryOffset + 272);
            var frames = view.ReadUInt32(entryOffset + 276);
            var frameTime = view.ReadUInt32(entryOffset + 280);
            var periodFps = 0.0;
            var instantFps = 0.0;

            if (time1 > time0 && frames > 0)
                periodFps = 1000.0 * frames / (time1 - time0);

            if (frameTime > 0)
                instantFps = 1000000.0 / frameTime;

            if (IsReasonableFps(instantFps))
            {
                if (!IsReasonableFps(periodFps))
                    return instantFps;

                if (instantFps <= periodFps * 2.0 || instantFps - periodFps <= 120.0)
                    return instantFps;
            }

            if (IsReasonableFps(periodFps))
                return periodFps;

            if (instantFps > MaxReasonableFps || periodFps > MaxReasonableFps)
                WriteMissingLog($"Rejected impossible RTSS FPS sample. instant={instantFps:0.0}, period={periodFps:0.0}, frameTimeUs={frameTime}, frames={frames}, timeDeltaMs={time1 - time0}.");

            return 0;
        }

        private static bool IsReasonableFps(double fps)
        {
            return fps >= 1.0 && fps <= MaxReasonableFps && !double.IsNaN(fps) && !double.IsInfinity(fps);
        }

        private double SmoothDisplayFps(double fps)
        {
            if (!IsReasonableFps(fps))
                return _displayFps;

            var now = DateTimeOffset.UtcNow;
            _displaySamples.Enqueue((now, fps));

            while (_displaySamples.Count > 0 && now - _displaySamples.Peek().Time > TimeSpan.FromMilliseconds(800))
                _displaySamples.Dequeue();

            var values = new List<double>(_displaySamples.Count);
            foreach (var sample in _displaySamples)
                values.Add(sample.Fps);

            values.Sort();
            var middle = values.Count / 2;
            var stableFps = values.Count % 2 == 0
                ? (values[middle - 1] + values[middle]) / 2.0
                : values[middle];
            var roundedFps = (int)Math.Round(stableFps);

            if (_displayFps > 0 && Math.Abs(roundedFps - _displayFps) <= 2)
                return _displayFps;

            if (_displayFps == 0 ||
                Math.Abs(roundedFps - _displayFps) >= 3 ||
                Math.Abs(stableFps - _displayFps) >= 2.5)
            {
                _displayFps = roundedFps;
            }

            return _displayFps;
        }

        private static string ReadEntryName(MemoryMappedViewAccessor view, long entryOffset)
        {
            var buffer = new byte[260];
            view.ReadArray(entryOffset + 4, buffer, 0, buffer.Length);
            var zero = Array.IndexOf(buffer, (byte)0);
            if (zero < 0)
                zero = buffer.Length;

            return Encoding.Default.GetString(buffer, 0, zero);
        }

        private void LogFpsSample(string appName, double fps)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastLogAt < TimeSpan.FromSeconds(1))
                return;

            _lastLogAt = now;
            WriteLog($"RTSS FPS sample: {fps:0.0} FPS for {appName} / process id {_targetProcessId}.");
        }

        private void WriteMissingLog(string message)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastMissingLogAt < TimeSpan.FromSeconds(2))
                return;

            _lastMissingLogAt = now;
            WriteLog(message);
        }

        private void WriteLog(string message)
        {
            try
            {
                var folder = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrWhiteSpace(folder))
                    Directory.CreateDirectory(folder);

                File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}{Environment.NewLine}");
            }
            catch
            {
                Debug.WriteLine($"[FPS] {message}");
            }
        }
    }
}
