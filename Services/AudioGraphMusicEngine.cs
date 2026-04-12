using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Media.Audio;
using Windows.Media.Render;
using Windows.Storage;

namespace Zink.Services
{
    /// <summary>
    /// AudioGraph-based music engine with a multi-band Equalizer.
    /// </summary>
    public sealed class AudioGraphMusicEngine
    {
        private static readonly Lazy<AudioGraphMusicEngine> _instance =
            new(() => new AudioGraphMusicEngine());

        public static AudioGraphMusicEngine Instance => _instance.Value;

        private AudioGraph _graph;
        private AudioDeviceOutputNode _deviceOutputNode;
        private AudioFileInputNode _fileInputNode;
        private EqualizerEffectDefinition _equalizer;

        private bool _initialized;
        private bool _isPlaying;

        // Logical user volume (0–1) from the slider
        private double _userVolume = 0.5;
        // Master gain from EQ in dB
        private double _masterGainDb = 0.0;

        private AudioGraphMusicEngine() { }

        public bool IsPlaying => _isPlaying;

        public TimeSpan Position =>
            _fileInputNode?.Position ?? TimeSpan.Zero;

        public TimeSpan Duration =>
            _fileInputNode?.Duration ?? TimeSpan.Zero;

        /// <summary>
        /// Ensure AudioGraph is created and ready.
        /// </summary>
        public async Task EnsureInitializedAsync()
        {
            if (_initialized) return;

            try
            {
                var settings = new AudioGraphSettings(AudioRenderCategory.Media);
                var result = await AudioGraph.CreateAsync(settings);

                if (result.Status != AudioGraphCreationStatus.Success)
                    throw new InvalidOperationException($"AudioGraph creation failed: {result.Status}");

                _graph = result.Graph;

                var deviceResult = await _graph.CreateDeviceOutputNodeAsync();
                if (deviceResult.Status != AudioDeviceNodeCreationStatus.Success)
                    throw new InvalidOperationException($"DeviceOutputNode creation failed: {deviceResult.Status}");

                _deviceOutputNode = deviceResult.DeviceOutputNode;

                _graph.Start();
                _initialized = true;

                // Create the equalizer definition once; we'll attach it per-file
                _equalizer = new EqualizerEffectDefinition(_graph);

                // Optionally restore saved EQ from settings
                RestoreEqFromSettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("AudioGraph initialization error: " + ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Returns the number of hardware EQ bands (ensures graph is initialized).
        /// </summary>
        public async Task<int> GetHardwareBandCountAsync()
        {
            try
            {
                await EnsureInitializedAsync();
                return _equalizer?.Bands?.Count ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Returns the hardware EQ band center frequencies in Hz.
        /// </summary>
        public async Task<double[]> GetHardwareBandFrequenciesAsync()
        {
            try
            {
                await EnsureInitializedAsync();

                if (_equalizer?.Bands == null || _equalizer.Bands.Count == 0)
                    return Array.Empty<double>();

                var bands = _equalizer.Bands;
                var result = new double[bands.Count];

                for (int i = 0; i < bands.Count; i++)
                {
                    result[i] = bands[i].FrequencyCenter;
                }

                return result;
            }
            catch
            {
                return Array.Empty<double>();
            }
        }

        public void SetUserVolume(double volume)
        {
            _userVolume = Math.Clamp(volume, 0.0, 1.0);
            UpdateGain();
        }

        public void SetMasterGain(double gainDb)
        {
            // Clamp master gain to a conservative range
            if (gainDb < -12) gainDb = -12;
            if (gainDb > 12) gainDb = 12;

            _masterGainDb = gainDb;
            UpdateGain();
        }

        /// <summary>
        /// Set EQ gains in dB. Adapts your 10 UI sliders to however many
        /// hardware bands exist, and clamps values so the platform is happy.
        /// </summary>
        public void SetBandGains(IReadOnlyList<double> gainsDb)
        {
            if (_equalizer == null || gainsDb == null) return;

            var bands = _equalizer.Bands;
            if (bands == null || bands.Count == 0) return;

            int hwCount = bands.Count;          // hardware bands (e.g. 4)
            int uiCount = gainsDb.Count;        // your sliders (10)
            if (uiCount == 0) return;

            for (int hw = 0; hw < hwCount; hw++)
            {
                // Map this hardware band to the closest UI slider index
                int uiIndex;
                if (hwCount == 1)
                {
                    uiIndex = 0;
                }
                else
                {
                    uiIndex = (int)Math.Round(
                        hw * (uiCount - 1) / (double)(hwCount - 1));
                }

                if (uiIndex < 0) uiIndex = 0;
                if (uiIndex >= uiCount) uiIndex = uiCount - 1;

                double g = gainsDb[uiIndex];

                // Conservative clamp (some devices are picky)
                if (g < -6) g = -6;
                if (g > 6) g = 6;

                try
                {
                    bands[hw].Gain = (float)g;
                }
                catch (ArgumentException ex)
                {
                    // Don't crash if the platform refuses this value
                    Debug.WriteLine($"Equalizer band[{hw}] gain {g} dB rejected: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Equalizer band[{hw}] unexpected error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Play the given file. Returns false if playback failed.
        /// </summary>
        public async Task<bool> PlayFileAsync(string path, TimeSpan? startPosition = null)
        {
            await EnsureInitializedAsync();

            try
            {
                // Stop and dispose previous file node
                if (_fileInputNode != null)
                {
                    _fileInputNode.Stop();
                    _fileInputNode.Dispose();
                    _fileInputNode = null;
                }

                var file = await StorageFile.GetFileFromPathAsync(path);
                if (file == null) return false;

                var inputResult = await _graph.CreateFileInputNodeAsync(file);
                if (inputResult.Status != AudioFileNodeCreationStatus.Success)
                {
                    Debug.WriteLine("CreateFileInputNodeAsync failed: " + inputResult.Status);
                    return false;
                }

                _fileInputNode = inputResult.FileInputNode;

                // Attach equalizer to this node
                if (_equalizer != null)
                {
                    _fileInputNode.EffectDefinitions.Clear();
                    _fileInputNode.EffectDefinitions.Add(_equalizer);
                }

                // Connect to output
                _fileInputNode.AddOutgoingConnection(_deviceOutputNode);

                // Position: use Seek instead of assigning Position (it is read-only)
                if (startPosition.HasValue &&
                    startPosition.Value >= TimeSpan.Zero &&
                    startPosition.Value < _fileInputNode.Duration)
                {
                    _fileInputNode.Seek(startPosition.Value);
                }

                UpdateGain();

                _fileInputNode.Start();
                _isPlaying = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("PlayFileAsync crash: " + ex.Message);
                return false;
            }
        }

        public void Pause()
        {
            if (_fileInputNode == null) return;
            _fileInputNode.Stop();
            _isPlaying = false;
        }

        public void Resume()
        {
            if (_fileInputNode == null) return;
            UpdateGain();
            _fileInputNode.Start();
            _isPlaying = true;
        }

        public void Stop()
        {
            if (_fileInputNode == null) return;
            _fileInputNode.Stop();
            _fileInputNode.Dispose();
            _fileInputNode = null;
            _isPlaying = false;
        }

        public void Seek(TimeSpan position)
        {
            if (_fileInputNode == null) return;

            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > _fileInputNode.Duration) position = _fileInputNode.Duration;

            // Use Seek API (Position is read-only)
            _fileInputNode.Seek(position);
        }

        private void UpdateGain()
        {
            if (_fileInputNode == null) return;

            // Convert master gain (dB) to linear multiplier
            double linearGain = Math.Pow(10.0, _masterGainDb / 20.0);
            double effective = _userVolume * linearGain;

            // Clamp to [0, 1.5] just to be safe
            if (effective < 0) effective = 0;
            if (effective > 1.5) effective = 1.5;

            _fileInputNode.OutgoingGain = (float)effective;
        }

        private void RestoreEqFromSettings()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("EQ_MasterGain", out object obj))
            {
                double gain = 0;
                if (obj is double d) gain = d;
                else if (obj is int i) gain = i;

                // Clamp any old stored values
                if (gain < -12) gain = -12;
                if (gain > 12) gain = 12;

                _masterGainDb = gain;
            }
        }
    }
}
