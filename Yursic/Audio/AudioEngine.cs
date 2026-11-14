using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;

namespace Yursic.Audio
{
    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }

    /// <summary>
    /// Real-time engine on top of AudioGraph. Playback is produced by our own <see cref="Mixer"/>
    /// through an AudioFrameInputNode, so effects and plugins run in our code. The default
    /// microphone (e.g. a headset or USB mic) is captured through an AudioFrameOutputNode.
    /// </summary>
    public sealed class AudioEngine
    {
        private AudioGraph _graph;
        private AudioDeviceOutputNode _output;
        private AudioFrameInputNode _playIn;
        private AudioDeviceInputNode _micIn;
        private AudioFrameOutputNode _micOut;
        private float[] _scratch = new float[0];

        private long _positionFrames;
        private volatile bool _playing;
        private volatile bool _recording;
        private readonly List<float> _recorded = new List<float>();
        private readonly object _recLock = new object();
        private int _micChannels = 2;

        public Mixer Mixer { get; } = new Mixer();
        public bool IsReady => _graph != null;
        public bool HasMicrophone => _micIn != null;
        public string MicrophoneError { get; private set; }
        public int SampleRate { get; private set; } = 48000;
        public int LatencyFrames => _graph?.LatencyInSamples ?? 0;
        public bool IsPlaying => _playing;
        public bool IsRecording => _recording;
        public volatile float InputMeter;

        public double PositionSeconds
        {
            get => (double)System.Threading.Interlocked.Read(ref _positionFrames) / SampleRate;
            set => System.Threading.Interlocked.Exchange(ref _positionFrames, (long)(Math.Max(0, value) * SampleRate));
        }

        public async Task InitAsync()
        {
            if (_graph != null) return;

            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency
            };
            var result = await AudioGraph.CreateAsync(settings);
            if (result.Status != AudioGraphCreationStatus.Success)
            {
                // Some devices refuse the lowest-latency quantum; fall back to the system default.
                settings.QuantumSizeSelectionMode = QuantumSizeSelectionMode.SystemDefault;
                result = await AudioGraph.CreateAsync(settings);
            }
            if (result.Status != AudioGraphCreationStatus.Success)
                throw new InvalidOperationException("Couldn't start audio: " + result.Status);
            _graph = result.Graph;
            SampleRate = (int)_graph.EncodingProperties.SampleRate;

            var outResult = await _graph.CreateDeviceOutputNodeAsync();
            if (outResult.Status != AudioDeviceNodeCreationStatus.Success)
                throw new InvalidOperationException("No audio output: " + outResult.Status);
            _output = outResult.DeviceOutputNode;

            var props = AudioEncodingProperties.CreatePcm((uint)SampleRate, 2, 32);
            props.Subtype = MediaEncodingSubtypes.Float;
            _playIn = _graph.CreateFrameInputNode(props);
            _playIn.AddOutgoingConnection(_output);
            _playIn.QuantumStarted += OnQuantumStarted;

            await TryCreateMicAsync();

            _graph.QuantumProcessed += OnQuantumProcessed;
            _graph.Start();
        }

        private async Task TryCreateMicAsync()
        {
            try
            {
                var micResult = await _graph.CreateDeviceInputNodeAsync(MediaCategory.Media);
                if (micResult.Status != AudioDeviceNodeCreationStatus.Success)
                {
                    MicrophoneError = micResult.Status == AudioDeviceNodeCreationStatus.AccessDenied
                        ? "Microphone access is off. Allow it in Settings > Privacy > Microphone."
                        : "No microphone found (" + micResult.Status + ").";
                    return;
                }
                _micIn = micResult.DeviceInputNode;
                var micProps = _graph.EncodingProperties;
                _micChannels = (int)micProps.ChannelCount;
                _micOut = _graph.CreateFrameOutputNode(micProps);
                _micIn.AddOutgoingConnection(_micOut);
            }
            catch (Exception ex)
            {
                MicrophoneError = "Microphone unavailable: " + ex.Message;
            }
        }

        public void Play() => _playing = true;

        public void Pause()
        {
            _playing = false;
            if (_recording) _recording = false;
        }

        public void StartRecording()
        {
            if (_micIn == null) throw new InvalidOperationException(MicrophoneError ?? "No microphone.");
            lock (_recLock) _recorded.Clear();
            _recording = true;
            _playing = true;
        }

        /// <summary>Stops recording and returns the captured stereo samples.</summary>
        public float[] StopRecording()
        {
            _recording = false;
            _playing = false;
            lock (_recLock)
            {
                var data = _recorded.ToArray();
                _recorded.Clear();
                return data;
            }
        }

        private unsafe void OnQuantumStarted(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
        {
            int frames = args.RequiredSamples;
            if (frames <= 0) return;
            int n = frames * 2;
            if (_scratch.Length < n) _scratch = new float[n];

            if (_playing)
            {
                long pos = System.Threading.Interlocked.Read(ref _positionFrames);
                try
                {
                    Mixer.Render(_scratch, frames, pos, SampleRate, true);
                }
                catch
                {
                    Array.Clear(_scratch, 0, n); // never let a bad plugin kill the audio thread
                }
                System.Threading.Interlocked.Add(ref _positionFrames, frames);
            }
            else
            {
                Array.Clear(_scratch, 0, n);
                Mixer.MasterMeter = 0;
            }

            var frame = new AudioFrame((uint)(n * sizeof(float)));
            using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (var reference = buffer.CreateReference())
            {
                ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* data, out uint capacity);
                float* f = (float*)data;
                int count = Math.Min(n, (int)(capacity / sizeof(float)));
                for (int i = 0; i < count; i++) f[i] = _scratch[i];
            }
            sender.AddFrame(frame);
        }

        private unsafe void OnQuantumProcessed(AudioGraph sender, object args)
        {
            if (_micOut == null) return;
            using (var frame = _micOut.GetFrame())
            using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Read))
            using (var reference = buffer.CreateReference())
            {
                ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* data, out uint capacity);
                int samples = (int)(buffer.Length / sizeof(float));
                float* f = (float*)data;
                int ch = Math.Max(1, _micChannels);
                int frames = samples / ch;
                float peak = 0;
                bool rec = _recording;
                if (rec) Monitor_Enter();
                try
                {
                    for (int i = 0; i < frames; i++)
                    {
                        float l = f[i * ch];
                        float r = ch > 1 ? f[i * ch + 1] : l;
                        float a = Math.Max(Math.Abs(l), Math.Abs(r));
                        if (a > peak) peak = a;
                        if (rec)
                        {
                            _recorded.Add(l);
                            _recorded.Add(r);
                        }
                    }
                }
                finally
                {
                    if (rec) Monitor_Exit();
                }
                InputMeter = peak;
            }
        }

        private void Monitor_Enter() => System.Threading.Monitor.Enter(_recLock);
        private void Monitor_Exit() => System.Threading.Monitor.Exit(_recLock);

        /// <summary>Offline render of the whole song (stop playback first).</summary>
        public float[] RenderOffline(double seconds, double tailSeconds = 2)
        {
            Mixer.ResetEffects();
            long total = (long)((seconds + tailSeconds) * SampleRate);
            var result = new float[total * 2];
            const int block = 1024;
            var tmp = new float[block * 2];
            for (long pos = 0; pos < total; pos += block)
            {
                int frames = (int)Math.Min(block, total - pos);
                Mixer.Render(tmp, frames, pos, SampleRate, false);
                Array.Copy(tmp, 0, result, pos * 2, frames * 2);
            }
            Mixer.ResetEffects();
            return result;
        }
    }
}
