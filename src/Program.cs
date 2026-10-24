using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.CoreAudioApi;

[assembly: System.Reflection.AssemblyVersion("1.3.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.3.0.0")]

namespace FifFloat
{
    internal static class Program
    {
        internal static void WriteJson(string path, object value)
        {
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "--diagnose")
                {
                    WriteJson(args[1], new { inputs = AudioEngine.Inputs(), outputs = AudioEngine.Outputs(), loopbackInputs = AudioEngine.LoopbackInputs(),
                        cableOutputId = AudioEngine.FindCableOutput(), framework = Environment.Version.ToString(),
                        defaults = ReadDefaults(), os = Environment.OSVersion.ToString(), time = DateTimeOffset.Now.ToString("o") });
                    return 0;
                }
                if (args.Length == 2 && args[0] == "--self-test") { SelfTest(args[1]); return 0; }
                if (args.Length == 2 && args[0] == "--probe-selection")
                {
                    System.Threading.Tasks.Task.Run(delegate { ProbeSelection(args[1]); }).GetAwaiter().GetResult();
                    return 0;
                }
                if (args.Length == 2 && args[0] == "--restore-speakers")
                {
                    string endpoint = SystemAudio.RestoreRealtekOutputIfCableIsDefault();
                    WriteJson(args[1], new { success = true, endpoint = endpoint, defaults = ReadDefaults(), time = DateTimeOffset.Now.ToString("o") });
                    return 0;
                }
                if (args.Length == 2 && args[0] == "--probe-cable")
                {
                    System.Threading.Tasks.Task.Run(delegate { ProbeCable(args[1]); }).GetAwaiter().GetResult();
                    return 0;
                }
                if (args.Length == 2 && args[0] == "--record-browser-test")
                {
                    System.Threading.Tasks.Task.Run(delegate { RecordBrowserTest(args[1]); }).GetAwaiter().GetResult();
                    return 0;
                }
                if (args.Length == 2 && args[0] == "--play-cable")
                {
                    System.Threading.Tasks.Task.Run(delegate
                    {
                        using (AudioEngine player = new AudioEngine(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "replay-test")))
                        {
                            player.LoadWav(args[1]);
                            player.StartPlayback(true, -1);
                            DateTime until = DateTime.UtcNow.AddSeconds(30);
                            while (player.Snapshot().Mode == "playing" && DateTime.UtcNow < until) Thread.Sleep(25);
                            Require(player.Snapshot().Mode == "idle", "虚拟回放超时");
                        }
                    }).GetAwaiter().GetResult();
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (FloatingForm form = new FloatingForm())
                {
                    if (args.Length == 2 && (args[0] == "--preview" || args[0] == "--preview-settings"))
                    {
                        form.ShowInTaskbar = false;
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-20000, -20000);
                        form.Show();
                        Application.DoEvents();
                        if (args[0] == "--preview-settings") { form.ExpandSettingsForPreview(); Application.DoEvents(); }
                        using (Graphics graphics = form.CreateGraphics())
                            WriteJson(args[1] + ".layout.json", new { dpi = graphics.DpiX, autoscale = form.AutoScaleDimensions.ToString(),
                                fontHeight = form.Font.GetHeight(graphics), client = form.ClientSize.ToString() });
                        using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                            bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                        }
                        form.ClosePreview();
                        return 0;
                    }
                    Application.Run(form);
                }
                return 0;
            }
            catch (Exception ex)
            {
                if (args.Length >= 2 && args[0].StartsWith("--"))
                {
                    WriteJson(args[1] + ".error.json", new { success = false, error = ex.ToString() });
                }
                else MessageBox.Show("程序未能完成操作：\r\n" + ex.Message, "FIF 录音悬浮窗", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        internal static void MakeTone(string path, double seconds)
        {
            MakeTone(path, seconds, 440);
        }

        internal static void MakeTone(string path, double seconds, double frequency)
        {
            using (WaveFileWriter wav = new WaveFileWriter(path, new WaveFormat(48000, 16, 1)))
            {
                int samples = (int)(48000 * seconds);
                byte[] bytes = new byte[samples * 2];
                for (int i = 0; i < samples; i++)
                {
                    short value = (short)(Math.Sin(2 * Math.PI * frequency * i / 48000.0) * 3276);
                    bytes[2 * i] = (byte)(value & 255);
                    bytes[2 * i + 1] = (byte)((value >> 8) & 255);
                }
                wav.Write(bytes, 0, bytes.Length);
            }
        }

        private static Dictionary<string, string> ReadDefaults()
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            try
            {
                using (MMDeviceEnumerator enumerator = new MMDeviceEnumerator())
                {
                    foreach (DataFlow flow in new[] { DataFlow.Render, DataFlow.Capture })
                        foreach (Role role in new[] { Role.Console, Role.Multimedia, Role.Communications })
                        {
                            using (MMDevice device = enumerator.GetDefaultAudioEndpoint(flow, role))
                                result[flow.ToString() + "/" + role.ToString()] = device.FriendlyName;
                        }
                }
            }
            catch (Exception ex) { result["error"] = ex.Message; }
            return result;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception("验证失败：" + message);
        }

        private static void SelfTest(string reportPath)
        {
            string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)), "test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string tone = Path.Combine(folder, "test-tone.wav");
            MakeTone(tone, 0.8);
            AudioEngine.ValidateWav(tone);
            List<string> checks = new List<string>();
            checks.Add("生成并验证 48kHz/16bit/mono WAV，实际解码长度正确");
            using (AudioEngine engine = new AudioEngine(Path.Combine(folder, "archive")))
            {
                engine.LoadWav(tone);
                Require(Math.Abs(engine.Snapshot().TotalSeconds - 0.8) < 0.001, "WAV 时长");
                Require(File.Exists(engine.Snapshot().FilePath), "导入副本持久化");
                checks.Add("导入后保存独立副本，读取时长和重启恢复");
                bool rejected = false;
                try { engine.StartRecording(WaveIn.DeviceCount + 10); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "拒绝已失效录音设备");
                checks.Add("拒绝无效录音设备，不开始采集");
                rejected = false;
                try { engine.StartRecording(-1); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "不能用默认设备绕过麦克风选择");
                checks.Add("拒绝不明身份的默认录音设备，避免录到虚拟通道");
                rejected = false;
                try { engine.StartPlayback(false, WaveOut.DeviceCount + 10); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "拒绝已失效播放设备");
                checks.Add("拒绝无效播放设备，不向默认扬声器回退");
                if (AudioEngine.FindCableOutput() < 0)
                {
                    rejected = false;
                    try { engine.StartPlayback(true, -1); } catch (InvalidOperationException) { rejected = true; }
                    Require(rejected && engine.Snapshot().Mode == "idle", "虚拟声卡缺失时 fail closed");
                    checks.Add("未安装虚拟声卡时拒绝播放给 FIF，保持 idle，不向扬声器回退");
                }
                string old = engine.Snapshot().FilePath;
                string invalid = Path.Combine(folder, "invalid.wav");
                using (WaveFileWriter wav = new WaveFileWriter(invalid, new WaveFormat(22050, 8, 1)))
                    wav.Write(new byte[100], 0, 100);
                rejected = false;
                try { engine.LoadWav(invalid); } catch (InvalidDataException) { rejected = true; }
                Require(rejected && old == engine.Snapshot().FilePath, "拒绝不兼容文件时保留当前录音");
                checks.Add("拒绝不兼容 WAV 时不覆盖当前录音");
            }
            using (AudioEngine restored = new AudioEngine(Path.Combine(folder, "archive")))
            {
                Require(restored.Snapshot().FilePath != null && Math.Abs(restored.Snapshot().TotalSeconds - 0.8) < 0.001, "启动恢复");
            }
            string older = Path.Combine(folder, "archive", "电脑声音_older.wav");
            string newer = Path.Combine(folder, "archive", "录音_newer.wav");
            File.Copy(tone, older);
            File.Copy(tone, newer);
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddSeconds(2));
            using (AudioEngine restored = new AudioEngine(Path.Combine(folder, "archive")))
                Require(restored.Snapshot().FilePath == newer, "混合电脑声音与麦克风录音时应恢复真正最新文件");
            checks.Add("电脑声音与麦克风文件混合时按修改时间恢复最近录音");
            byte[] values = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes(0.5f), 0, values, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(-0.5f), 0, values, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(float.NaN), 0, values, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(2f), 0, values, 12, 4);
            byte[] pcm = AudioEngine.ConvertToPcm16(values, values.Length, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
            Require(BitConverter.ToInt16(pcm, 0) == 16384 && BitConverter.ToInt16(pcm, 2) == -16384 &&
                BitConverter.ToInt16(pcm, 4) == 0 && BitConverter.ToInt16(pcm, 6) == 32767, "浮点立体声转换与限幅");
            checks.Add("内录浮点立体声正确转为PCM16，非数值归零并限制溢出");
            Require(AudioEngine.IsCableInput("CABLE Input (VB-Audio Virtual Cable)"), "正确声卡名称");
            Require(!AudioEngine.IsCableInput("Speakers (Realtek)") && !AudioEngine.IsCableInput("CABLE Output (VB-Audio Virtual Cable)"), "区分 input/output");
            checks.Add("准确区分虚拟播放端、录音端与实体扬声器");
            TestRecordingLibrary(folder, tone, checks);
            TestDeletionLibrary(folder, tone, checks);
            WriteJson(reportPath, new { success = true, time = DateTimeOffset.Now.ToString("o"), checks = checks,
                microphoneCaptured = false, physicalSpeakerPlayed = false, fifTested = false });
        }

        private static void TestRecordingLibrary(string folder, string tone, List<string> checks)
        {
            string own = Path.Combine(folder, "library");
            Directory.CreateDirectory(own);
            string first = Path.Combine(own, "first.wav");
            string second = Path.Combine(own, "second.wav");
            File.Copy(tone, first);
            File.Copy(tone, second);
            RecordingLibrary library = new RecordingLibrary(own);
            Require(library.Items().Count == 2, "扫描已有多条 WAV");
            library.Add(first);
            Require(library.Items().Count == 2, "重复加入同文件不应新增一行");
            library.Rename(first, "第 1 题");
            RecordingLibrary restored = new RecordingLibrary(own);
            Require(restored.Items().Find(delegate(RecordingClip clip) { return clip.FilePath == first; }).Name == "第 1 题", "名称重启保存");
            restored.Remove(first);
            Require(File.Exists(first), "从列表移除不能删除 WAV");
            Require(new RecordingLibrary(own).Items().Count == 1, "移出的文件重启后不应自动重现");
            restored.Add(first);
            Require(new RecordingLibrary(own).Items().Count == 2, "明确重新加入恢复移出的录音");
            restored.Rename(first, "第 1 题");
            bool rejected = false;
            try { restored.Add(tone); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "外部文件须先经过导入复制");
            string before = File.ReadAllText(Path.Combine(own, "录音列表.json"), Encoding.UTF8);
            restored.Items()[0].Name = "外部修改副本";
            Require(File.ReadAllText(Path.Combine(own, "录音列表.json"), Encoding.UTF8) == before, "列表读取返回副本");
            using (AudioEngine player = new AudioEngine(own))
            {
                int originalCount = Directory.GetFiles(own, "*.wav").Length;
                player.SelectRecording(first);
                Require(player.Snapshot().FilePath == first, "点播选择目标录音");
                player.SelectRecording(second);
                Require(player.Snapshot().FilePath == second && Directory.GetFiles(own, "*.wav").Length == originalCount,
                    "选择已有录音不能重复复制音频");
                rejected = false;
                try { player.SelectRecording(tone); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "点播不能绕过录音目录边界");
            }
            string index = Path.Combine(own, "录音列表.json");
            before = File.ReadAllText(index, Encoding.UTF8);
            using (FileStream locked = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                rejected = false;
                try { new RecordingLibrary(own); } catch (IOException) { rejected = true; }
                Require(rejected && File.ReadAllText(index, Encoding.UTF8) == before, "暂态文件占用不得抹掉名称索引");
            }
            Require(new RecordingLibrary(own).Items().Count == 2, "文件解锁后列表可恢复");
            File.WriteAllText(index + ".bak", before, Encoding.UTF8);
            File.WriteAllText(index, "{broken", Encoding.UTF8);
            Require(new RecordingLibrary(own).Items().Find(delegate(RecordingClip clip) { return clip.FilePath == first; }).Name == "第 1 题",
                "主索引损坏可从有效备份恢复名称");
            Require(File.ReadAllText(index + ".bak", Encoding.UTF8) == before, "恢复不能用损坏索引覆盖健康备份");
            before = File.ReadAllText(index, Encoding.UTF8);
            using (FileStream locked = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                rejected = false;
                try { new RecordingLibrary(own); } catch (IOException) { rejected = true; }
                Require(rejected, "索引暂时被锁定时不能用旧备份覆盖");
            }
            Require(File.ReadAllText(index, Encoding.UTF8) == before, "索引锁定后原文不变");
            File.WriteAllText(index, "{\"Version\":99,\"NextNumber\":50,\"Clips\":[]}", Encoding.UTF8);
            string future = File.ReadAllText(index, Encoding.UTF8);
            rejected = false;
            try { new RecordingLibrary(own); } catch (NotSupportedException) { rejected = true; }
            Require(rejected && File.ReadAllText(index, Encoding.UTF8) == future, "未知版本索引不可覆盖");
            File.WriteAllText(index, before, Encoding.UTF8);
            checks.Add("多条录音扫描、去重、改名持久化与移除保留音频");
            checks.Add("移出列表重启后保持移出，可明确重新加入；选择播放不复制原文件");
            checks.Add("录音占用和未知索引版本保持原元数据，点播目录边界有效");
        }

        private static void TestDeletionLibrary(string folder, string tone, List<string> checks)
        {
            string own = Path.Combine(folder, "deletion");
            Directory.CreateDirectory(own);
            string[] files = new string[5];
            for (int i = 0; i < 3; i++)
            {
                files[i] = Path.Combine(own, "clip" + i + ".wav");
                File.Copy(tone, files[i]);
                File.SetLastWriteTimeUtc(files[i], DateTime.UtcNow.AddMinutes(i - 10));
            }
            RecordingLibrary library = new RecordingLibrary(own);
            Require(library.Items()[2].Name == "录音 3", "初始默认编号");
            library.Delete(files[1]);
            List<RecordingClip> clips = library.Items();
            Require(clips.Count == 2 && clips[0].Name == "录音 1" && clips[1].Name == "录音 2", "删除中间项后编号补齐");
            Require(!File.Exists(files[1]) && Directory.GetFiles(own, "*.wav", SearchOption.AllDirectories).Length == 2 &&
                !Directory.Exists(Path.Combine(own, "已删除")), "直接删除音频，不生成归档或额外副本");
            library.Rename(files[2], "第 3 题");
            files[3] = Path.Combine(own, "clip3.wav");
            File.Copy(tone, files[3]);
            library.Add(files[3]);
            Require(library.Items()[2].Name == "录音 2", "新增接续补齐后的默认编号");
            library.Delete(files[0]);
            library = new RecordingLibrary(own);
            Require(library.Items()[0].Name == "第 3 题" && library.Items()[1].Name == "录音 1", "自定义名保留，编号重启仍连续");
            library.Rename(files[3], "录音 88");
            library.Delete(files[2]);
            Require(new RecordingLibrary(own).Items()[0].Name == "录音 88", "手改为默认样式的名称也应保留");
            files[4] = Path.Combine(own, "clip4.wav");
            File.Copy(tone, files[4]);
            library.Add(files[4]);
            string index = Path.Combine(own, "录音列表.json");
            string before = File.ReadAllText(index, Encoding.UTF8);
            bool rejected;
            using (FileStream locked = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                rejected = false;
                try { library.Delete(files[4]); } catch (IOException) { rejected = true; }
                Require(rejected && File.Exists(files[4]) && library.Items().Count == 2 && library.Items()[1].Name == "录音 1",
                    "索引写入失败应恢复原音频及编号");
            }
            Require(File.ReadAllText(index, Encoding.UTF8) == before && Directory.GetFiles(own, "*.wav", SearchOption.AllDirectories).Length == 2,
                "删除失败不改变索引且不产生额外副本");
            using (FileStream locked = new FileStream(files[4], FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                rejected = false;
                try { library.Delete(files[4]); } catch (IOException) { rejected = true; }
                Require(rejected && File.ReadAllText(index, Encoding.UTF8) == before && library.Items().Count == 2, "文件被占用不能先删列表");
            }
            Require(File.ReadAllText(index + ".bak", Encoding.UTF8) == before, "删除失败回滚保留删除前健康备份");
            library.Delete(files[4]);
            library.Delete(files[3]);
            Require(new RecordingLibrary(own).Items().Count == 0, "删除全部后重启不复现");
            File.Copy(tone, files[0]);
            Require(library.Add(files[0]).Name == "录音 1", "空列表重新录入从1开始");
            string legacy = Path.Combine(folder, "legacy-library");
            Directory.CreateDirectory(legacy);
            File.Copy(tone, Path.Combine(legacy, "one.wav"));
            File.Copy(tone, Path.Combine(legacy, "two.wav"));
            File.Copy(tone, Path.Combine(legacy, "three.wav"));
            File.WriteAllText(Path.Combine(legacy, "录音列表.json"), "{\"Version\":1,\"NextNumber\":9,\"Clips\":[{\"FileName\":\"one.wav\",\"Name\":\"录音 3\"},{\"FileName\":\"two.wav\",\"Name\":\"自定义题目\"},{\"FileName\":\"three.wav\",\"Name\":\"录音 88\"}]}", Encoding.UTF8);
            RecordingLibrary upgraded = new RecordingLibrary(legacy);
            Require(upgraded.Items()[0].Name == "录音 1" && upgraded.Items()[0].AutoNamed && upgraded.Items()[1].Name == "自定义题目" && !upgraded.Items()[1].AutoNamed,
                "旧版索引迁移保留自定义名称并补齐默认编号");
            Require(upgraded.Items()[2].Name == "录音 88" && !upgraded.Items()[2].AutoNamed, "旧版超出生成范围的默认样式名称保留");
            checks.Add("删除中间、首条、全部后默认编号自动补齐，新录音接续编号，重启保持");
            checks.Add("自定义名称和手改为默认样式的名称保留；v1索引平滑迁移");
            checks.Add("直接删除文件且不保留副本；占用或索引失败保留文件并恢复列表及编号");
        }

        private static void ProbeSelection(string reportPath)
        {
            int inputId = -1;
            for (int i = 0; i < WaveIn.DeviceCount; i++)
                if (WaveIn.GetCapabilities(i).ProductName.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase)) { inputId = i; break; }
            Require(inputId >= 0 && AudioEngine.FindCableOutput() >= 0, "没有可用的虚拟声卡，未打开实体设备");
            string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)), "selection_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            double[] frequencies = { 440, 660, 880 };
            using (AudioEngine player = new AudioEngine(Path.Combine(folder, "player")))
            using (AudioEngine recorder = new AudioEngine(Path.Combine(folder, "capture")))
            {
                RecordingLibrary library = new RecordingLibrary(player.DataDirectory);
                for (int i = 0; i < frequencies.Length; i++)
                {
                    string tone = Path.Combine(folder, "tone_" + i + ".wav");
                    MakeTone(tone, 0.55, frequencies[i]);
                    player.LoadWav(tone);
                    RecordingClip clip = library.Add(player.Snapshot().FilePath);
                    library.Rename(clip.FilePath, "测试段 " + (i + 1));
                }
                List<RecordingClip> items = new RecordingLibrary(player.DataDirectory).Items();
                List<object> played = new List<object>();
                foreach (int selected in new[] { 2, 0, 1 })
                {
                    recorder.StartVirtualCaptureForTest(inputId);
                    Thread.Sleep(120);
                    player.StopPlayback();
                    player.SelectRecording(items[selected].FilePath);
                    player.StartPlayback(true, -1);
                    DateTime until = DateTime.UtcNow.AddSeconds(5);
                    while (player.Snapshot().Mode == "playing" && DateTime.UtcNow < until) Thread.Sleep(20);
                    Require(player.Snapshot().Mode == "idle", "所选录音播放未及时结束");
                    Thread.Sleep(150);
                    recorder.StopRecording();
                    byte[] bytes;
                    using (WaveFileReader wav = new WaveFileReader(recorder.Snapshot().FilePath))
                    { bytes = new byte[(int)wav.Length]; wav.Read(bytes, 0, bytes.Length); }
                    double desired = TonePower(bytes, frequencies[selected], 48000);
                    double other = 0;
                    for (int i = 0; i < frequencies.Length; i++)
                        if (i != selected) other = Math.Max(other, TonePower(bytes, frequencies[i], 48000));
                    Require(desired > 10000 && desired / Math.Max(1, other) > 30, "虚拟麦克风收到的不是选中的那一条");
                    Require(player.Snapshot().FilePath == items[selected].FilePath, "选中条目与正在播放文件不一致");
                    played.Add(new { selectedIndex = selected + 1, name = items[selected].Name,
                        expectedHz = frequencies[selected], signalPower = desired, otherPower = other });
                }
                Require(library.Items().Count == 3, "播放后列表必须保留所有录音");
                WriteJson(reportPath, new { success = true, played = played, microphoneCaptured = false,
                    physicalSpeakerPlayed = false, fifTested = false, time = DateTimeOffset.Now.ToString("o") });
            }
        }

        private static double TonePower(byte[] data, double frequency, int rate)
        {
            double re = 0, im = 0;
            int count = data.Length / 2;
            for (int i = 0; i < count; i++)
            {
                short value = (short)(data[i * 2] | (data[i * 2 + 1] << 8));
                double angle = 2 * Math.PI * frequency * i / rate;
                re += value * Math.Cos(angle);
                im += value * Math.Sin(angle);
            }
            return (re * re + im * im) / Math.Max(1, count * (double)count);
        }

        private static void RecordBrowserTest(string reportPath)
        {
            string endpoint = null;
            using (MMDeviceEnumerator enumerator = new MMDeviceEnumerator())
                foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    using (device)
                        if (device.FriendlyName.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase)) { endpoint = device.ID; break; }
            Require(endpoint != null, "测试只能捕获虚拟 CABLE Input");
            string folder = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            using (AudioEngine recorder = new AudioEngine(Path.Combine(folder, "browser-loopback-recordings")))
            {
                string previous = recorder.Snapshot().FilePath;
                recorder.StartRecording(new AudioDevice(-2, "虚拟输出自检") { IsLoopback = true, EndpointId = endpoint });
                WriteJson(reportPath + ".ready", new { ready = true });
                DateTime until = DateTime.UtcNow.AddSeconds(15);
                while (!File.Exists(reportPath + ".stop") && DateTime.UtcNow < until) Thread.Sleep(25);
                Require(File.Exists(reportPath + ".stop"), "浏览器测试音未在时限内结束");
                recorder.StopRecording();
                AudioSnapshot state = recorder.Snapshot();
                Require(String.IsNullOrEmpty(state.Error) && state.FilePath != null && state.FilePath != previous, "内录保存失败");
                AudioEngine.ValidateWav(state.FilePath);
                int rate, channels;
                byte[] mono;
                using (WaveFileReader wav = new WaveFileReader(state.FilePath))
                {
                    rate = wav.WaveFormat.SampleRate;
                    channels = wav.WaveFormat.Channels;
                    byte[] all = new byte[(int)wav.Length];
                    wav.Read(all, 0, all.Length);
                    mono = new byte[all.Length / channels];
                    for (int i = 0; i < mono.Length / 2; i++)
                    {
                        mono[2 * i] = all[2 * i * channels];
                        mono[2 * i + 1] = all[2 * i * channels + 1];
                    }
                }
                double best = 0, contrast = 0;
                int window = rate / 5 * 2;
                for (int offset = 0; offset + window <= mono.Length; offset += window / 2)
                {
                    byte[] part = new byte[window];
                    Buffer.BlockCopy(mono, offset, part, 0, window);
                    double power = TonePower(part, 440, rate);
                    if (power > best) { best = power; contrast = TonePower(part, 1000, rate); }
                }
                int first = -1, last = -1, gap = 0, maximumGap = 0;
                for (int i = 0; i < mono.Length / 2; i++)
                {
                    if (Math.Abs((int)BitConverter.ToInt16(mono, i * 2)) > 100)
                    {
                        if (first < 0) first = i;
                        maximumGap = Math.Max(maximumGap, gap);
                        gap = 0;
                        last = i;
                    }
                    else if (first >= 0) gap++;
                }
                double silence = maximumGap / (double)rate;
                Require(best > 10000 && best / Math.Max(1, contrast) > 30, "内录 WAV 中未识别浏览器的 440Hz 声音");
                Require(silence > 0.15 && last > first, "网页两次播放之间的停顿没有保留");
                WriteJson(reportPath, new { success = true, recordedPath = state.FilePath, seconds = state.TotalSeconds,
                    sampleRate = rate, channels = channels, tonePower = best, contrastPower = contrast, retainedPauseSeconds = silence,
                    captureSource = state.CaptureSource, physicalMicrophoneOpened = false, physicalSpeakerPlayed = false,
                    fifTested = false, time = DateTimeOffset.Now.ToString("o") });
            }
        }

        // Explicitly uses ONLY the virtual devices. No physical microphone or speaker is opened.
        private static void ProbeCable(string reportPath)
        {
            int outputId = AudioEngine.FindCableOutput();
            int inputId = -1;
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                string name = WaveIn.GetCapabilities(i).ProductName;
                if (name.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase) && name.Contains("VB-Audio")) { inputId = i; break; }
            }
            Require(outputId >= 0 && inputId >= 0, "没有检测到完整 VB-CABLE 设备对");
            string folder = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            string tone = Path.Combine(folder, "virtual-probe-tone.wav");
            MakeTone(tone, 0.8);
            string recordedPath;
            using (AudioEngine recorder = new AudioEngine(Path.Combine(folder, "virtual-recording-test")))
            using (AudioEngine player = new AudioEngine(Path.Combine(folder, "virtual-playback-test")))
            {
                player.LoadWav(tone);
                recorder.StartVirtualCaptureForTest(inputId);
                Thread.Sleep(200);
                player.StartPlayback(true, -1);
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (player.Snapshot().Mode == "playing" && DateTime.UtcNow < deadline) Thread.Sleep(30);
                Require(player.Snapshot().Mode == "idle", "播放未及时结束");
                using (FileStream unlocked = new FileStream(player.Snapshot().FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Require(unlocked.Length > 0, "自然播放完成后未释放文件句柄");
                Thread.Sleep(250);
                recorder.StopRecording();
                Require(String.IsNullOrEmpty(recorder.Snapshot().Error), "录音过程异常");
                recordedPath = recorder.Snapshot().FilePath;
                byte[] bytes;
                using (WaveFileReader captured = new WaveFileReader(recordedPath))
                {
                    bytes = new byte[(int)captured.Length];
                    int count = captured.Read(bytes, 0, bytes.Length);
                    Require(count == bytes.Length, "WAV 未完整保存");
                }
                double signal = TonePower(bytes, 440, 48000);
                double other = Math.Max(TonePower(bytes, 600, 48000), TonePower(bytes, 1000, 48000));
                Require(bytes.Length > 48000 && signal > 10000 && signal / Math.Max(1, other) > 30, "未能从虚拟录音端识别 440Hz 测试音");
                recorder.StartVirtualCaptureForTest(inputId);
                Thread.Sleep(180);
                recorder.Dispose();
                Require(File.Exists(recorder.Snapshot().FilePath) && Directory.GetFiles(recorder.DataDirectory, "*.part").Length == 0,
                    "录音中关闭未能完整封口保存");
                AudioEngine.ValidateWav(recorder.Snapshot().FilePath);
                WriteJson(reportPath, new { success = true, input = WaveIn.GetCapabilities(inputId).ProductName,
                    output = WaveOut.GetCapabilities(outputId).ProductName, capturedSeconds = bytes.Length / 96000.0,
                    tonePower = signal, contrastPower = other, microphoneCaptured = false,
                    checks = new[] { "实际引擎录音与播放闭环", "WAV 正确封口保存", "自然播放后释放文件", "录音中关闭会完成保存" },
                    physicalSpeakerPlayed = false, fifTested = false, time = DateTimeOffset.Now.ToString("o") });
            }
        }
    }
}
