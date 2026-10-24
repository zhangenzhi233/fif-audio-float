using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;

namespace FifFloat
{
    internal static class UiProbe
    {
        static object Field(FloatingForm form, string name) { return typeof(FloatingForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form); }
        static void Set(FloatingForm form, string name, object value) { typeof(FloatingForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, value); }
        static void Call(FloatingForm form, string name, object value) { typeof(FloatingForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { value }); }
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Pump(int milliseconds) { DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds); do { Application.DoEvents(); Thread.Sleep(10); } while (DateTime.UtcNow < end); }
        static void Await(Task task) { DateTime end = DateTime.UtcNow.AddSeconds(10); while (!task.IsCompleted && DateTime.UtcNow < end) Pump(15); Require(task.IsCompleted, "operation timed out"); task.GetAwaiter().GetResult(); }
        [STAThread]
        static int Main(string[] args)
        {
            List<string> checks = new List<string>();
            try
            {
                string data = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "录音");
                Directory.CreateDirectory(data);
                for (int i = 0; i < 3; i++) Program.MakeTone(Path.Combine(data, "test" + i + ".wav"), 2.0, 440 + i * 220);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                FloatingForm form = new FloatingForm();
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-20000, -20000);
                form.ShowInTaskbar = false;
                int result = 0;
                form.Shown += delegate
                {
                  form.BeginInvoke(new MethodInvoker(delegate
                  {
                   try
                   {
                    Require(SynchronizationContext.Current is WindowsFormsSynchronizationContext, "UI message loop context missing");
                    FlowLayoutPanel rows = (FlowLayoutPanel)Field(form, "_clipRows");
                    RecordingLibrary library = (RecordingLibrary)Field(form, "_library");
                    AudioEngine engine = (AudioEngine)Field(form, "_engine");
                    Require(library.Items().Count == 3, "three seeded clips were not loaded");
                    foreach (int selected in new int[] { 2, 0, 1 })
                    {
                        string expected = library.Items()[selected].FilePath;
                        Button target = null;
                        foreach (Control control in rows.Controls[selected].Controls)
                            if (control is Button && control.Text == "播放") target = (Button)control;
                        Require(target != null && target.Enabled, "row play button unavailable");
                        target.PerformClick();
                        DateTime end = DateTime.UtcNow.AddSeconds(8);
                        while ((bool)Field(form, "_busy") && DateTime.UtcNow < end) Pump(20);
                        Require(!(bool)Field(form, "_busy") && engine.Snapshot().Mode == "playing" && engine.Snapshot().FilePath == expected, "row selected wrong file");
                        Await(Task.Run(delegate { engine.StopPlayback(); })); Pump(50);
                    }
                    checks.Add("实际列表行播放按钮按3、1、2点击，均选中对应音频并开始虚拟端播放");
                    Button playSelected = null;
                    foreach (Control control in rows.Controls[1].Controls)
                        if (control is Button && control.Text == "播放") playSelected = (Button)control;
                    playSelected.PerformClick();
                    while ((bool)Field(form, "_busy")) Pump(20);
                    Pump(200);
                    string deletedPath = engine.Snapshot().FilePath;
                    Button deleteSelected = null;
                    foreach (Control control in rows.Controls[1].Controls)
                        if (control is Button && control.Text == "删除") deleteSelected = (Button)control;
                    Require(deleteSelected != null && deleteSelected.Enabled, "delete button unavailable; rows=" + rows.Enabled + "; busy=" + Field(form, "_busy") +
                        "; mode=" + engine.Snapshot().Mode + "; button=" + (deleteSelected == null ? "missing" : deleteSelected.Enabled.ToString()));
                    deleteSelected.PerformClick();
                    while ((bool)Field(form, "_busy")) Pump(20);
                    Pump(50);
                    Require(engine.Snapshot().Mode == "idle" && library.Items().Count == 2 && !File.Exists(deletedPath), "delete while playing failed");
                    Require(library.Items()[0].Name == "录音 1" && library.Items()[1].Name == "录音 2" && !Directory.Exists(Path.Combine(data, "已删除")),
                        "deleted audio was archived or numbering incorrect");
                    checks.Add("点击正在播放行的删除按钮会先停播，再直接删除文件，剩余默认名称补齐");
                    while (library.Items().Count > 0)
                    {
                        Button deleteFirst = null;
                        foreach (Control control in rows.Controls[0].Controls)
                            if (control is Button && control.Text == "删除") deleteFirst = (Button)control;
                        deleteFirst.PerformClick();
                        while ((bool)Field(form, "_busy")) Pump(20);
                        Pump(30);
                    }
                    Require(!((Button)Field(form, "_fifButton")).Enabled && ((Label)Field(form, "_timeLabel")).Text == "00:00", "empty list playback/time state incorrect");
                    Require(new RecordingLibrary(data).Items().Count == 0, "deleted clips reappeared after restart");
                    checks.Add("删除全部后播放禁用、时间清零，重启不复现已删除音频");
                    int input = -1;
                    for (int i = 0; i < WaveIn.DeviceCount; i++)
                        if (WaveIn.GetCapabilities(i).ProductName.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase)) { input = i; break; }
                    Require(input >= 0, "virtual capture endpoint missing");
                    ((System.Windows.Forms.Timer)Field(form, "_timer")).Stop();
                    Set(form, "_recordingFileBefore", engine.Snapshot().FilePath);
                    Set(form, "_awaitingCompletedRecording", true);
                    Await(Task.Run(delegate { engine.StartVirtualCaptureForTest(input); }));
                    Pump(350);
                    Await(Task.Run(delegate { engine.StopRecording(); }));
                    // No recording snapshot was delivered to the form: exercises rapid completion.
                    Call(form, "UpdateView", engine.Snapshot());
                    Require(library.Items().Count == 1 && library.Items()[0].Name == "录音 1", "completed capture not added with numbering reset");
                    Call(form, "UpdateView", engine.Snapshot());
                    Require(library.Items().Count == 1, "same completed capture added twice");
                    Require(new RecordingLibrary(data).Items().Count == 1, "new clip did not persist");
                    checks.Add("首个界面快照前已结束的虚拟录音可入库，重复刷新不重复添加，重启仍保存");
                    Call(form, "UpdateButtons", new AudioSnapshot { Mode = "recording" });
                    Require(!rows.Enabled, "recording must disable row playback");
                    checks.Add("录音期间整组列表点播禁用");
                    form.ClosePreview();
                   }
                   catch (Exception ex)
                   {
                       result = 1;
                       Program.WriteJson(args[0], new { success = false, error = ex.ToString() });
                       form.ClosePreview();
                   }
                  }));
                };
                Application.Run(form);
                form.Dispose();
                if (result != 0) return result;
                Program.WriteJson(args[0], new { success = true, checks = checks, microphoneCaptured = false, physicalSpeakerPlayed = false, fifTested = false, time = DateTimeOffset.Now.ToString("o") });
                return 0;
            }
            catch (Exception ex)
            {
                Program.WriteJson(args[0], new { success = false, error = ex.ToString() });
                return 1;
            }
        }
    }
}
