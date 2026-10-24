using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace FifFloat
{
    public sealed class AudioDevice
    {
        public int Id;
        public string Name;
        public bool IsLoopback;
        public string EndpointId;
        public AudioDevice(int id, string name) { Id = id; Name = name; }
        public override string ToString() { return Name; }
    }

    public sealed class AudioSnapshot
    {
        public string Mode, FilePath, Error, CaptureSource;
        public double Seconds, TotalSeconds;
        public float Peak;
    }

    // All start/stop operations are invoked on a worker thread by the form.
    // Playback for FIF NEVER uses the default endpoint or an arbitrary output.
    public sealed class AudioEngine : IDisposable
    {
        private readonly object sync = new object();
        private readonly object dataSync = new object();
        private IWaveIn input;
        private MMDevice loopbackDevice;
        private WasapiOut loopbackKeepAlive;
        private WaveFormat capturedFormat;
        private int recordingBytesPerSecond = BytesPerSecond;
        private string captureSource = "microphone";
        private WaveFileWriter writer;
        private ManualResetEventSlim recordStopped;
        private WaveOutEvent output;
        private WaveFileReader reader;
        private volatile bool recording, playing;
        private bool disposed;
        private string filePath, recordingPath, lastError;
        private long capturedBytes;
        private double totalSeconds;
        private float peak;
        private const int SampleRate = 48000;
        private const int BytesPerSecond = SampleRate * 2;
        public string DataDirectory { get; private set; }

        public AudioEngine() : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "录音")) { }

        public AudioEngine(string dataDirectory)
        {
            DataDirectory = Path.GetFullPath(dataDirectory);
            Directory.CreateDirectory(DataDirectory);
            // Restore a completed recording, never a .part file left by a crash.
            string[] files = Directory.GetFiles(DataDirectory, "*.wav");
            Array.Sort(files, delegate(string left, string right)
            {
                int byTime = File.GetLastWriteTimeUtc(left).CompareTo(File.GetLastWriteTimeUtc(right));
                return byTime != 0 ? byTime : StringComparer.OrdinalIgnoreCase.Compare(left, right);
            });
            for (int i = files.Length - 1; i >= 0; i--)
            {
                try { ValidateWav(files[i]); filePath = files[i]; totalSeconds = GetDuration(filePath); break; }
                catch (Exception) { /* An unreadable file cannot become the current clip. */ }
            }
        }

        public static List<AudioDevice> Inputs()
        {
            List<AudioDevice> result = new List<AudioDevice>();
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                string name = WaveIn.GetCapabilities(i).ProductName;
                if (!IsVirtualCapture(name)) result.Add(new AudioDevice(i, name));
            }
            return result;
        }

        public static List<AudioDevice> Outputs()
        {
            List<AudioDevice> result = new List<AudioDevice>();
            result.Add(new AudioDevice(-1, "系统默认输出（试听会出声）"));
            for (int i = 0; i < WaveOut.DeviceCount; i++)
                result.Add(new AudioDevice(i, WaveOut.GetCapabilities(i).ProductName));
            return result;
        }

        public static int FindCableOutput()
        {
            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                string name = WaveOut.GetCapabilities(i).ProductName;
                if (IsCableInput(name)) return i;
            }
            return -1;
        }

        public static List<AudioDevice> LoopbackInputs()
        {
            List<AudioDevice> result = new List<AudioDevice>();
            result.Add(new AudioDevice(-2, "跟随 Windows 当前播放设备") { IsLoopback = true });
            using (MMDeviceEnumerator enumerator = new MMDeviceEnumerator())
            {
                foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    using (device)
                    {
                        if (IsVirtualCapture(device.FriendlyName)) continue;
                        result.Add(new AudioDevice(-2, device.FriendlyName) { IsLoopback = true, EndpointId = device.ID });
                    }
            }
            return result;
        }

        public static string DefaultPlaybackName()
        {
            using (MMDeviceEnumerator enumerator = new MMDeviceEnumerator())
            using (MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                return device.FriendlyName;
        }

        public void StartRecording(AudioDevice device)
        {
            if (device == null) throw new InvalidOperationException("请先选择录音来源设备。");
            if (device.IsLoopback) StartLoopbackRecording(device.EndpointId);
            else StartRecording(device.Id);
        }

        public void StartLoopbackRecording(string endpointId)
        {
            lock (sync)
            {
                CheckDisposed();
                if (recording) throw new InvalidOperationException("当前正在录音，请先结束录音。");
                StopPlaybackCore();
                CleanupInput();
                BeginRecordingFile("computer");
                try
                {
                    using (MMDeviceEnumerator enumerator = new MMDeviceEnumerator())
                        loopbackDevice = String.IsNullOrEmpty(endpointId)
                            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                            : enumerator.GetDevice(endpointId);
                    if (loopbackDevice.DataFlow != DataFlow.Render || loopbackDevice.State != DeviceState.Active)
                        throw new InvalidOperationException("所选播放设备已不可用，请刷新并选择浏览器正在使用的耳机或扬声器。");
                    WasapiLoopbackCapture capture = new WasapiLoopbackCapture(loopbackDevice);
                    input = capture;
                    capturedFormat = capture.WaveFormat.AsStandardWaveFormat();
                    if ((capturedFormat.Encoding != WaveFormatEncoding.IeeeFloat && capturedFormat.Encoding != WaveFormatEncoding.Pcm) ||
                        (capturedFormat.BitsPerSample != 8 && capturedFormat.BitsPerSample != 16 && capturedFormat.BitsPerSample != 24 && capturedFormat.BitsPerSample != 32) ||
                        (capturedFormat.Encoding == WaveFormatEncoding.IeeeFloat && capturedFormat.BitsPerSample != 32) ||
                        capturedFormat.Channels < 1 || capturedFormat.Channels > 32 || capturedFormat.SampleRate < 8000 || capturedFormat.SampleRate > 96000)
                        throw new InvalidOperationException("该播放设备格式暂不支持，请使用 8–96 kHz 的扬声器或耳机。");
                    int channels = capturedFormat.Channels > 2 ? 1 : capturedFormat.Channels;
                    WaveFormat savedFormat = new WaveFormat(capturedFormat.SampleRate, 16, channels);
                    recordingBytesPerSecond = savedFormat.AverageBytesPerSecond;
                    writer = new WaveFileWriter(recordingPath + ".part", savedFormat);
                    input.DataAvailable += OnDataAvailable;
                    input.RecordingStopped += OnRecordingStopped;
                    // Zero-only rendering keeps the clock moving during browser pauses.
                    // It never monitors captured audio and cannot produce audible feedback.
                    loopbackKeepAlive = new WasapiOut(loopbackDevice, AudioClientShareMode.Shared, false, 80);
                    loopbackKeepAlive.Init(new SilenceProvider(capturedFormat));
                    loopbackKeepAlive.PlaybackStopped += OnKeepAliveStopped;
                    recording = true;
                    input.StartRecording();
                    loopbackKeepAlive.Play();
                }
                catch
                {
                    recording = false;
                    CleanupInput();
                    lock (dataSync) { if (writer != null) { writer.Dispose(); writer = null; } }
                    if (File.Exists(recordingPath + ".part")) File.Delete(recordingPath + ".part");
                    throw;
                }
            }
        }

        private void BeginRecordingFile(string source)
        {
            recordingPath = Path.Combine(DataDirectory, (source == "computer" ? "电脑声音_" : "录音_") + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".wav");
            capturedBytes = 0;
            peak = 0;
            lastError = null;
            captureSource = source;
            recordStopped = new ManualResetEventSlim(false);
        }

        public static bool IsCableInput(string name)
        {
            return !String.IsNullOrEmpty(name) &&
                name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase) &&
                name.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsVirtualCapture(string name)
        {
            return name.StartsWith("CABLE", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOf("Steam Streaming", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void CheckDisposed()
        {
            if (disposed) throw new ObjectDisposedException("AudioEngine");
        }

        public void StartRecording(int deviceId)
        {
            StartRecordingCore(deviceId, false);
        }

        internal void StartVirtualCaptureForTest(int deviceId)
        {
            if (deviceId < 0 || deviceId >= WaveIn.DeviceCount ||
                !WaveIn.GetCapabilities(deviceId).ProductName.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("自检只允许打开 CABLE Output，不允许打开实体麦克风。");
            StartRecordingCore(deviceId, true);
        }

        private void StartRecordingCore(int deviceId, bool allowVirtualForTest)
        {
            lock (sync)
            {
                CheckDisposed();
                if (recording) throw new InvalidOperationException("当前正在录音，请先结束录音。");
                StopPlaybackCore();
                CleanupInput();
                if (deviceId < 0 || deviceId >= WaveIn.DeviceCount)
                    throw new InvalidOperationException("录音设备已变化，请刷新设备后重选。");
                if (deviceId >= 0)
                {
                    string name = WaveIn.GetCapabilities(deviceId).ProductName;
                    if (!allowVirtualForTest && IsVirtualCapture(name))
                        throw new InvalidOperationException("请选真实麦克风录制自己的声音，不要选择 CABLE Output。");
                }
                BeginRecordingFile("microphone");
                try
                {
                    WaveInEvent microphone = new WaveInEvent();
                    microphone.DeviceNumber = deviceId;
                    microphone.WaveFormat = new WaveFormat(SampleRate, 16, 1);
                    microphone.BufferMilliseconds = 60;
                    microphone.NumberOfBuffers = 3;
                    input = microphone;
                    capturedFormat = microphone.WaveFormat;
                    recordingBytesPerSecond = capturedFormat.AverageBytesPerSecond;
                    writer = new WaveFileWriter(recordingPath + ".part", input.WaveFormat);
                    input.DataAvailable += OnDataAvailable;
                    input.RecordingStopped += OnRecordingStopped;
                    recording = true;
                    input.StartRecording();
                }
                catch
                {
                    recording = false;
                    CleanupInput();
                    lock (dataSync) { if (writer != null) { writer.Dispose(); writer = null; } }
                    if (File.Exists(recordingPath + ".part")) File.Delete(recordingPath + ".part");
                    throw;
                }
            }
        }

        private void OnDataAvailable(object sender, WaveInEventArgs args)
        {
            try
            {
                lock (dataSync)
                {
                    if (writer == null) return;
                    byte[] data = args.Buffer;
                    int byteCount = args.BytesRecorded;
                    if (captureSource == "computer") { data = ConvertToPcm16(args.Buffer, args.BytesRecorded, capturedFormat); byteCount = data.Length; }
                    writer.Write(data, 0, byteCount);
                    capturedBytes += byteCount;
                    int maximum = 0;
                    for (int i = 0; i + 1 < byteCount; i += 2)
                    {
                        int sample = (short)(data[i] | (data[i + 1] << 8));
                        maximum = Math.Max(maximum, Math.Abs(sample));
                    }
                    peak = maximum / 32768f;
                }
            }
            catch (Exception ex)
            {
                lastError = "录音写入失败：" + ex.Message;
                // NAudio cannot be disposed from inside its own capture callback.
                ThreadPool.QueueUserWorkItem(delegate { try { StopRecording(); } catch (Exception) { } });
            }
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs args)
        {
            try
            {
                lock (dataSync)
                {
                    if (writer != null) { writer.Dispose(); writer = null; }
                    if (args.Exception != null) lastError = "录音设备错误：" + args.Exception.Message;
                    string partial = recordingPath + ".part";
                    if (File.Exists(partial) && capturedBytes > 0)
                    {
                        File.Move(partial, recordingPath);
                        filePath = recordingPath;
                        totalSeconds = capturedBytes / (double)recordingBytesPerSecond;
                    }
                    else if (File.Exists(partial))
                    {
                        File.Delete(partial);
                        if (String.IsNullOrEmpty(lastError)) lastError = "没有收到录音数据，请检查声音来源设备。";
                    }
                    recording = false;
                }
            }
            catch (Exception ex) { recording = false; lastError = "保存录音失败：" + ex.Message; }
            finally
            {
                if (recordStopped != null) recordStopped.Set();
                ThreadPool.QueueUserWorkItem(delegate
                {
                    lock (sync)
                        if (Object.ReferenceEquals(input, sender) && !recording) CleanupInput();
                });
            }
        }

        public void StopRecording()
        {
            lock (sync)
            {
                if (input == null) { CleanupInput(); return; }
                input.StopRecording();
                if (recordStopped != null && !recordStopped.Wait(5000))
                    throw new IOException("录音设备未及时结束，请稍后再试；原录音文件仍保留在录音目录。");
                CleanupInput();
            }
        }

        private void CleanupInput()
        {
            IWaveIn previousInput = input;
            WasapiOut previousKeepAlive = loopbackKeepAlive;
            MMDevice previousDevice = loopbackDevice;
            ManualResetEventSlim previousSignal = recordStopped;
            input = null;
            loopbackKeepAlive = null;
            loopbackDevice = null;
            recordStopped = null;
            Exception failure = null;
            if (previousInput != null)
            {
                previousInput.DataAvailable -= OnDataAvailable;
                previousInput.RecordingStopped -= OnRecordingStopped;
                try { previousInput.Dispose(); } catch (Exception ex) { failure = ex; }
            }
            if (previousKeepAlive != null)
            {
                previousKeepAlive.PlaybackStopped -= OnKeepAliveStopped;
                try { previousKeepAlive.Stop(); } catch (Exception ex) { failure = failure ?? ex; }
                try { previousKeepAlive.Dispose(); } catch (Exception ex) { failure = failure ?? ex; }
            }
            if (previousDevice != null) { try { previousDevice.Dispose(); } catch (Exception ex) { failure = failure ?? ex; } }
            if (previousSignal != null) { try { previousSignal.Dispose(); } catch (Exception ex) { failure = failure ?? ex; } }
            if (failure != null) lastError = (lastError ?? "") + " 音频设备释放异常：" + failure.Message;
        }

        private void OnKeepAliveStopped(object sender, StoppedEventArgs args)
        {
            if (args.Exception == null) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (sync)
                {
                    if (!Object.ReferenceEquals(loopbackKeepAlive, sender)) return;
                    lastError = "播放设备已断开：" + args.Exception.Message;
                    try { StopRecording(); } catch (Exception ex) { lastError += " " + ex.Message; }
                }
            });
        }

        internal static byte[] ConvertToPcm16(byte[] data, int count, WaveFormat format)
        {
            int sampleBytes = format.BitsPerSample / 8;
            int sourceChannels = format.Channels;
            int targetChannels = sourceChannels > 2 ? 1 : sourceChannels;
            int frames = count / format.BlockAlign;
            byte[] converted = new byte[frames * targetChannels * 2];
            for (int frame = 0; frame < frames; frame++)
            {
                for (int target = 0; target < targetChannels; target++)
                {
                    double value = 0;
                    int first = targetChannels == 1 && sourceChannels > 2 ? 0 : target;
                    int last = sourceChannels > 2 ? sourceChannels : first + 1;
                    for (int channel = first; channel < last; channel++)
                    {
                        int offset = frame * format.BlockAlign + channel * sampleBytes;
                        double sample;
                        if (format.Encoding == WaveFormatEncoding.IeeeFloat) sample = BitConverter.ToSingle(data, offset);
                        else if (sampleBytes == 1) sample = (data[offset] - 128) / 128.0;
                        else if (sampleBytes == 2) sample = BitConverter.ToInt16(data, offset) / 32768.0;
                        else if (sampleBytes == 3)
                        {
                            int number = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
                            if ((number & 0x800000) != 0) number |= unchecked((int)0xFF000000);
                            sample = number / 8388608.0;
                        }
                        else sample = BitConverter.ToInt32(data, offset) / 2147483648.0;
                        if (Double.IsNaN(sample) || Double.IsInfinity(sample)) sample = 0;
                        value += sample;
                    }
                    value /= last - first;
                    short pcm = (short)Math.Round(Math.Max(-1, Math.Min(32767.0 / 32768, value)) * 32768);
                    int destination = (frame * targetChannels + target) * 2;
                    converted[destination] = (byte)(pcm & 255);
                    converted[destination + 1] = (byte)((pcm >> 8) & 255);
                }
            }
            return converted;
        }

        public void SelectRecording(string path)
        {
            lock (sync)
            {
                CheckDisposed();
                if (recording) throw new InvalidOperationException("请先结束当前录音。");
                string full = Path.GetFullPath(path);
                string own = Path.GetFullPath(DataDirectory).TrimEnd(Path.DirectorySeparatorChar);
                if (!String.Equals(Path.GetDirectoryName(full), own, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("请先将外部录音导入录音列表。");
                ValidateWav(full);
                StopPlaybackCore();
                filePath = full;
                totalSeconds = GetDuration(full);
                lastError = null;
            }
        }

        public void LoadWav(string path)
        {
            lock (sync)
            {
                CheckDisposed();
                if (recording) throw new InvalidOperationException("请先结束录音。");
                string full = Path.GetFullPath(path);
                ValidateWav(full);
                StopPlaybackCore();
                // Copy into our own archive, so later playback never depends on a removable file.
                string destination = Path.Combine(DataDirectory, "导入_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".wav");
                File.Copy(full, destination, false);
                filePath = destination;
                totalSeconds = GetDuration(destination);
                lastError = null;
            }
        }

        public static void ValidateWav(string path)
        {
            using (WaveFileReader wav = new WaveFileReader(path))
            {
                if (wav.WaveFormat.Encoding != WaveFormatEncoding.Pcm || wav.WaveFormat.BitsPerSample != 16 ||
                    (wav.WaveFormat.Channels != 1 && wav.WaveFormat.Channels != 2) ||
                    wav.WaveFormat.SampleRate < 8000 || wav.WaveFormat.SampleRate > 96000 ||
                    wav.Length == 0 || wav.Length > 120L * 1024 * 1024)
                    throw new InvalidDataException("请使用 16 位 PCM WAV（单声道或双声道，8–96 kHz，最大 120 MB）。本工具录制的文件可直接播放。");
            }
        }

        private static double GetDuration(string path)
        {
            using (WaveFileReader wav = new WaveFileReader(path)) { return wav.TotalTime.TotalSeconds; }
        }

        public void StartPlayback(bool toCable, int previewDeviceId)
        {
            lock (sync)
            {
                CheckDisposed();
                if (recording) throw new InvalidOperationException("请先结束录音，再播放。");
                if (String.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    throw new InvalidOperationException("请先录一段声音，或导入 WAV 文件。");
                int id = toCable ? FindCableOutput() : previewDeviceId;
                if (toCable && id < 0)
                    throw new InvalidOperationException("没有检测到 CABLE Input。请安装 VB-CABLE 并重启，再刷新设备。没有向扬声器播放。");
                if (id < -1 || id >= WaveOut.DeviceCount)
                    throw new InvalidOperationException("播放设备已变化，请刷新设备后重选。");
                if (!toCable && (id < 0 || IsVirtualCapture(WaveOut.GetCapabilities(id).ProductName)))
                    throw new InvalidOperationException("本地试听请选择真实扬声器或耳机，不能使用默认设备或虚拟通道。");
                // Confirm identity just before opening; never accept a stale index for the FIF path.
                if (toCable && !IsCableInput(WaveOut.GetCapabilities(id).ProductName))
                    throw new InvalidOperationException("虚拟声卡设备已变化，请刷新后重试。");
                ValidateWav(filePath);
                StopPlaybackCore();
                try
                {
                    reader = new WaveFileReader(filePath);
                    output = new WaveOutEvent();
                    output.DeviceNumber = id;
                    output.DesiredLatency = 100;
                    output.NumberOfBuffers = 3;
                    output.PlaybackStopped += OnPlaybackStopped;
                    output.Init(reader);
                    lastError = null;
                    totalSeconds = reader.TotalTime.TotalSeconds;
                    playing = true;
                    output.Play();
                }
                catch { StopPlaybackCore(); throw; }
            }
        }

        private void OnPlaybackStopped(object sender, StoppedEventArgs args)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (sync)
                {
                    if (!Object.ReferenceEquals(output, sender)) return;
                    if (args.Exception != null) lastError = "播放设备错误：" + args.Exception.Message;
                    StopPlaybackCore();
                }
            });
        }

        public void StopPlayback() { lock (sync) { StopPlaybackCore(); } }

        private void StopPlaybackCore()
        {
            playing = false;
            if (output != null)
            {
                output.PlaybackStopped -= OnPlaybackStopped;
                output.Stop();
                output.Dispose();
                output = null;
            }
            if (reader != null) { reader.Dispose(); reader = null; }
        }

        public AudioSnapshot Snapshot()
        {
            lock (sync)
            {
                AudioSnapshot state = new AudioSnapshot();
                lock (dataSync)
                {
                    state.Mode = recording ? "recording" : (playing ? "playing" : "idle");
                    state.FilePath = filePath;
                    state.Error = lastError;
                    state.TotalSeconds = totalSeconds;
                    state.Peak = recording ? peak : 0;
                    state.CaptureSource = captureSource;
                    state.Seconds = recording ? capturedBytes / (double)recordingBytesPerSecond : totalSeconds;
                }
                if (!recording && output != null && reader != null)
                {
                    try { state.Seconds = Math.Min(output.GetPosition() / (double)reader.WaveFormat.AverageBytesPerSecond, state.TotalSeconds); }
                    catch (Exception) { state.Seconds = 0; }
                }
                return state;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                StopRecording();
                StopPlaybackCore();
                disposed = true;
            }
        }
    }
}
