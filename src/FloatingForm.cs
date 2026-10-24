using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FifFloat
{
    /// <summary>
    /// A small always-on-top helper. It never drives the FIF web page: the user
    /// deliberately starts FIF's recorder, then starts this tool's cable playback.
    /// </summary>
    public class FloatingForm : Form
    {
        private readonly AudioEngine _engine;
        private readonly RecordingLibrary _library;
        private readonly Timer _timer;
        private readonly ComboBox _captureSource;
        private readonly ComboBox _inputDevices;
        private readonly ComboBox _previewDevices;
        private readonly Button _recordButton;
        private readonly Button _fifButton;
        private readonly Button _previewButton;
        private readonly Button _refreshButton;
        private readonly Button _folderButton;
        private readonly Button _importButton;
        private readonly Button _installButton;
        private readonly LinkLabel _settingsLink;
        private readonly Panel _settingsPanel;
        private readonly Label _statusLabel;
        private readonly Label _timeLabel;
        private readonly Label _fileLabel;
        private readonly Label _cableLabel;
        private readonly ProgressBar _progress;
        private readonly ProgressBar _level;
        private readonly Label _guideLabel;
        private readonly Label _sourceHint;
        private readonly ToolTip _toolTip;
        private readonly Panel _libraryPanel;
        private readonly FlowLayoutPanel _clipRows;
        private readonly Label _libraryCaption;

        private bool _busy;
        private bool _settingsVisible;
        private bool _closeRequested;
        private bool _closeFinalizing;
        private bool _closingInternally;
        private bool _hasCable;
        private bool _playingToFif;
        private bool _awaitingCompletedRecording;
        private string _recordingFileBefore;
        private string _pendingLibraryPath;
        private bool _libraryAddProblem;
        private string _lastMode = "idle";
        private RecordingClip _selectedClip;
        private Point _dragStart;
        private bool _dragging;

        public FloatingForm()
        {
            _engine = new AudioEngine();
            _library = new RecordingLibrary(_engine.DataDirectory);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            Text = "FIF 录音小窗 · 多录音版";
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(430, 610);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Color.FromArgb(246, 249, 253);

            Panel titleBar = new Panel();
            titleBar.BackColor = Color.FromArgb(18, 56, 101);
            titleBar.Location = new Point(0, 0);
            titleBar.Size = new Size(ClientSize.Width, 48);
            titleBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            titleBar.Cursor = Cursors.SizeAll;
            titleBar.MouseDown += DragStart;
            titleBar.MouseMove += DragMove;
            titleBar.MouseUp += DragEnd;
            Controls.Add(titleBar);

            Label title = MakeLabel("FIF 口语录音助手", 14F, FontStyle.Bold, Color.White);
            title.Location = new Point(16, 9);
            title.Size = new Size(250, 27);
            title.Cursor = Cursors.SizeAll;
            title.MouseDown += DragStart;
            title.MouseMove += DragMove;
            title.MouseUp += DragEnd;
            titleBar.Controls.Add(title);

            _statusLabel = MakeLabel("准备就绪", 9F, FontStyle.Regular, Color.FromArgb(35, 80, 135));
            _statusLabel.Location = new Point(18, 61);
            _statusLabel.Size = new Size(365, 21);
            Controls.Add(_statusLabel);

            _cableLabel = MakeLabel("正在检测虚拟声卡…", 8.6F, FontStyle.Regular, Color.FromArgb(115, 125, 140));
            _cableLabel.Location = new Point(18, 83);
            _cableLabel.Size = new Size(365, 19);
            Controls.Add(_cableLabel);

            Label sourceCaption = MakeLabel("录音来源", 8.6F, FontStyle.Regular, Color.FromArgb(74, 84, 100));
            sourceCaption.Location = new Point(18, 105);
            sourceCaption.Size = new Size(88, 25);
            Controls.Add(sourceCaption);
            _captureSource = new ComboBox();
            _captureSource.DropDownStyle = ComboBoxStyle.DropDownList;
            _captureSource.Items.Add("录电脑声音（浏览器等）");
            _captureSource.Items.Add("录麦克风");
            _captureSource.SelectedIndex = 0;
            _captureSource.Location = new Point(106, 101);
            _captureSource.Size = new Size(275, 27);
            Controls.Add(_captureSource);
            _sourceHint = MakeLabel("包含浏览器及其他电脑声音；想安静可用耳机。", 8.1F, FontStyle.Regular, Color.FromArgb(93, 103, 118));
            _sourceHint.Location = new Point(18, 132);
            _sourceHint.Size = new Size(363, 18);
            Controls.Add(_sourceHint);

            _timeLabel = MakeLabel("00:00", 24F, FontStyle.Bold, Color.FromArgb(20, 55, 96));
            _timeLabel.TextAlign = ContentAlignment.MiddleCenter;
            _timeLabel.Location = new Point(18, 148);
            _timeLabel.Size = new Size(366, 40);
            Controls.Add(_timeLabel);

            _progress = new ProgressBar();
            _progress.Location = new Point(21, 192);
            _progress.Size = new Size(360, 8);
            _progress.Style = ProgressBarStyle.Continuous;
            Controls.Add(_progress);

            _level = new ProgressBar();
            _level.Location = new Point(21, 206);
            _level.Size = new Size(360, 6);
            _level.Maximum = 100;
            _level.Style = ProgressBarStyle.Continuous;
            Controls.Add(_level);

            _fileLabel = MakeLabel("当前录音：尚未录制或导入 WAV", 8.5F, FontStyle.Regular, Color.FromArgb(93, 103, 118));
            _fileLabel.Location = new Point(21, 223);
            _fileLabel.Size = new Size(360, 19);
            _fileLabel.AutoEllipsis = true;
            Controls.Add(_fileLabel);

            _recordButton = MakeButton("开始录音", Color.FromArgb(35, 110, 190));
            _recordButton.Location = new Point(21, 251);
            _recordButton.Size = new Size(174, 39);
            _recordButton.Click += RecordClicked;
            Controls.Add(_recordButton);

            _fifButton = MakeButton("播放给 FIF", Color.FromArgb(24, 139, 119));
            _fifButton.Location = new Point(207, 251);
            _fifButton.Size = new Size(174, 39);
            _fifButton.Click += FifClicked;
            Controls.Add(_fifButton);

            _previewButton = MakeButton("本地试听（扬声器可能出声）", Color.FromArgb(91, 105, 126));
            _previewButton.Location = new Point(21, 298);
            _previewButton.Size = new Size(360, 36);
            _previewButton.Click += PreviewClicked;
            Controls.Add(_previewButton);

            _guideLabel = MakeLabel("先点 FIF 网页的录音，再点“播放给 FIF”。\r\n网页麦克风选：CABLE Output。", 8.6F, FontStyle.Regular, Color.FromArgb(76, 88, 105));
            _guideLabel.Location = new Point(21, 344);
            _guideLabel.Size = new Size(360, 42);
            Controls.Add(_guideLabel);

            _settingsLink = new LinkLabel();
            _settingsLink.Text = "设备与设置  ▾";
            _settingsLink.LinkColor = Color.FromArgb(35, 93, 162);
            _settingsLink.ActiveLinkColor = Color.FromArgb(17, 69, 131);
            _settingsLink.Location = new Point(21, 382);
            _settingsLink.Size = new Size(160, 23);
            _settingsLink.LinkClicked += SettingsClicked;
            Controls.Add(_settingsLink);

            Button edgeSettings = MakeButton("复制麦克风设置地址", Color.FromArgb(73, 109, 150));
            edgeSettings.Location = new Point(220, 377);
            edgeSettings.Size = new Size(161, 29);
            edgeSettings.Click += delegate
            {
                try
                {
                    Clipboard.SetText("edge://settings/content/microphone");
                    MessageBox.Show(this, "已复制 Edge 麦克风设置地址。\r\n\r\n在 Edge 地址栏粘贴并按回车，麦克风选择：\r\nCABLE Output (VB-Audio Virtual Cable)。\r\n\r\n返回 FIF 后刷新页面，再开始录音。", "配置 FIF 麦克风", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { ShowError(ex); }
            };
            Controls.Add(edgeSettings);
            _toolTip = new ToolTip();
            _toolTip.SetToolTip(edgeSettings, "复制地址后，在 Edge 地址栏粘贴并回车，再选择 CABLE Output。");

            _settingsPanel = new Panel();
            _settingsPanel.Location = new Point(18, 407);
            _settingsPanel.Size = new Size(394, 150);
            _settingsPanel.Visible = false;
            Controls.Add(_settingsPanel);

            Label inputCaption = MakeLabel("录音来源设备", 8.5F, FontStyle.Regular, Color.FromArgb(74, 84, 100));
            inputCaption.Location = new Point(3, 1);
            inputCaption.Size = new Size(90, 20);
            _settingsPanel.Controls.Add(inputCaption);
            _inputDevices = new ComboBox();
            _inputDevices.DropDownStyle = ComboBoxStyle.DropDownList;
            _inputDevices.Location = new Point(3, 21);
            _inputDevices.Size = new Size(388, 26);
            _settingsPanel.Controls.Add(_inputDevices);
            _captureSource.SelectedIndexChanged += CaptureSourceChanged;

            Label previewCaption = MakeLabel("本地试听输出", 8.5F, FontStyle.Regular, Color.FromArgb(74, 84, 100));
            previewCaption.Location = new Point(3, 54);
            previewCaption.Size = new Size(110, 20);
            _settingsPanel.Controls.Add(previewCaption);
            _previewDevices = new ComboBox();
            _previewDevices.DropDownStyle = ComboBoxStyle.DropDownList;
            _previewDevices.Location = new Point(3, 74);
            _previewDevices.Size = new Size(388, 26);
            _settingsPanel.Controls.Add(_previewDevices);

            _refreshButton = MakeButton("刷新", Color.FromArgb(73, 109, 150));
            _refreshButton.Location = new Point(3, 110);
            _refreshButton.Size = new Size(83, 30);
            _refreshButton.Click += RefreshClicked;
            _settingsPanel.Controls.Add(_refreshButton);
            _folderButton = MakeButton("录音目录", Color.FromArgb(73, 109, 150));
            _folderButton.Location = new Point(91, 110);
            _folderButton.Size = new Size(112, 30);
            _folderButton.Click += FolderClicked;
            _settingsPanel.Controls.Add(_folderButton);
            _importButton = MakeButton("导入", Color.FromArgb(73, 109, 150));
            _importButton.Location = new Point(208, 110);
            _importButton.Size = new Size(75, 30);
            _importButton.Click += ImportClicked;
            _settingsPanel.Controls.Add(_importButton);
            _installButton = MakeButton("说明", Color.FromArgb(129, 91, 45));
            _installButton.Location = new Point(288, 110);
            _installButton.Size = new Size(75, 30);
            _installButton.Click += InstallClicked;
            _settingsPanel.Controls.Add(_installButton);

            _libraryCaption = MakeLabel("录音列表 · 点哪条播哪条", 9.5F, FontStyle.Bold, Color.FromArgb(20, 55, 96));
            _libraryCaption.Location = new Point(21, 410);
            _libraryCaption.Size = new Size(300, 22);
            Controls.Add(_libraryCaption);
            _libraryPanel = new Panel();
            _libraryPanel.BackColor = Color.White;
            _libraryPanel.BorderStyle = BorderStyle.FixedSingle;
            _libraryPanel.Location = new Point(18, 434);
            _libraryPanel.Size = new Size(394, 154);
            Controls.Add(_libraryPanel);
            _clipRows = new FlowLayoutPanel();
            _clipRows.Dock = DockStyle.Fill;
            _clipRows.AutoScroll = true;
            _clipRows.FlowDirection = FlowDirection.TopDown;
            _clipRows.WrapContents = false;
            _clipRows.Padding = new Padding(2);
            _libraryPanel.Controls.Add(_clipRows);

            _timer = new Timer();
            _timer.Interval = 150;
            _timer.Tick += TimerTick;
            Shown += FormShown;
            FormClosing += FormIsClosing;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
        }

        public void SavePreview(string path)
        {
            using (Bitmap bitmap = new Bitmap(Width, Height))
            {
                DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                bitmap.Save(path);
            }
        }

        public void ExpandSettingsForPreview() { SettingsClicked(null, null); }

        public void ClosePreview()
        {
            _timer.Stop();
            _engine.Dispose();
            _closingInternally = true;
            Close();
        }

        private void FormShown(object sender, EventArgs e)
        {
            RefreshDevices();
            RefreshClipList();
            UpdateView(_engine.Snapshot());
            _timer.Start();
        }

        private void RefreshClipList()
        {
            string selectedPath = _selectedClip == null ? null : _selectedClip.FilePath;
            List<RecordingClip> clips = _library.Items();
            _selectedClip = null;
            _clipRows.SuspendLayout();
            ClearClipRows();
            for (int i = 0; i < clips.Count; i++)
            {
                RecordingClip clip = clips[i];
                if (selectedPath != null && String.Equals(selectedPath, clip.FilePath, StringComparison.OrdinalIgnoreCase)) _selectedClip = clip;
            }
            if (_selectedClip == null && clips.Count > 0) _selectedClip = clips[clips.Count - 1];
            for (int i = 0; i < clips.Count; i++) AddClipRow(clips[i]);
            if (clips.Count == 0)
            {
                Label empty = MakeLabel("还没有录音。开始/结束录音，或在“设备与设置”中导入 WAV。", 8.5F, FontStyle.Regular, Color.FromArgb(115, 125, 140));
                empty.Size = new Size(365, 42);
                empty.Margin = new Padding(5);
                ScaleDynamicControl(empty);
                _clipRows.Controls.Add(empty);
            }
            _clipRows.ResumeLayout();
        }

        private void AddClipRow(RecordingClip clip)
        {
            Panel row = new Panel();
            row.Size = new Size(370, 39);
            row.Margin = new Padding(1, 1, 1, 1);
            row.BackColor = _selectedClip != null && String.Equals(_selectedClip.FilePath, clip.FilePath, StringComparison.OrdinalIgnoreCase)
                ? Color.FromArgb(232, 241, 251) : Color.White;
            row.Cursor = Cursors.Hand;
            row.Click += delegate { SelectClip(clip); };
            row.DoubleClick += delegate { RenameClip(clip); };

            Label name = MakeLabel(clip.Name, 8.8F, FontStyle.Regular, Color.FromArgb(31, 54, 83));
            name.AutoEllipsis = true;
            name.Location = new Point(5, 2);
            name.Size = new Size(128, 18);
            _toolTip.SetToolTip(name, clip.Name);
            name.Cursor = Cursors.Hand;
            name.Click += delegate { SelectClip(clip); };
            name.DoubleClick += delegate { RenameClip(clip); };
            row.Controls.Add(name);

            Label duration = MakeLabel(TimeText(clip.DurationSeconds), 8F, FontStyle.Regular, Color.FromArgb(103, 113, 128));
            duration.Location = new Point(5, 20);
            duration.Size = new Size(60, 16);
            duration.Click += delegate { SelectClip(clip); };
            row.Controls.Add(duration);

            Button rename = MakeSmallButton("改名", Color.FromArgb(103, 122, 146));
            rename.Location = new Point(139, 5);
            rename.Size = new Size(40, 28);
            rename.Click += delegate { RenameClip(clip); };
            row.Controls.Add(rename);

            Button play = MakeSmallButton("播放", Color.FromArgb(24, 139, 119));
            play.Location = new Point(184, 5);
            play.Size = new Size(62, 28);
            play.Click += async delegate { await PlayClipToFif(clip); };
            row.Controls.Add(play);

            Button preview = MakeSmallButton("试听", Color.FromArgb(91, 105, 126));
            preview.Location = new Point(251, 5);
            preview.Size = new Size(48, 28);
            preview.Click += async delegate { await PreviewClip(clip); };
            row.Controls.Add(preview);

            Button delete = MakeSmallButton("删除", Color.FromArgb(170, 69, 64));
            delete.Location = new Point(304, 5);
            delete.Size = new Size(50, 28);
            delete.Click += async delegate { await DeleteClip(clip); };
            _toolTip.SetToolTip(delete, "直接删除这条录音文件，并补齐默认名称；手动改过的名称保留。");
            row.Controls.Add(delete);

            ContextMenu menu = new ContextMenu();
            MenuItem remove = new MenuItem("移出列表（保留 WAV 文件）");
            remove.Click += delegate { RemoveClip(clip); };
            menu.MenuItems.Add(remove);
            row.ContextMenu = menu;
            name.ContextMenu = menu;
            duration.ContextMenu = menu;
            ScaleDynamicControl(row);
            _clipRows.Controls.Add(row);
        }

        private void ClearClipRows()
        {
            while (_clipRows.Controls.Count > 0)
            {
                Control old = _clipRows.Controls[_clipRows.Controls.Count - 1];
                ContextMenu menu = old.ContextMenu;
                old.ContextMenu = null;
                _clipRows.Controls.Remove(old);
                old.Dispose();
                if (menu != null) menu.Dispose();
            }
        }

        private void ScaleDynamicControl(Control control)
        {
            float scale = ClientSize.Width / 430F;
            if (Math.Abs(scale - 1F) > 0.01F) control.Scale(new SizeF(scale, scale));
        }

        private void SelectClip(RecordingClip clip)
        {
            if (_busy || clip == null) return;
            _selectedClip = clip;
            _fileLabel.Text = "已选择：" + clip.Name;
            RefreshClipList();
            UpdateButtons(_engine.Snapshot());
        }

        private void RemoveClip(RecordingClip clip)
        {
            if (_busy || clip == null) return;
            try
            {
                _library.Remove(clip.FilePath);
                if (_selectedClip != null && String.Equals(_selectedClip.FilePath, clip.FilePath, StringComparison.OrdinalIgnoreCase)) _selectedClip = null;
                RefreshClipList();
                UpdateButtons(_engine.Snapshot());
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private async Task DeleteClip(RecordingClip clip)
        {
            if (_busy || clip == null || _engine.Snapshot().Mode == "recording") return;
            await DoOperation("正在删除“" + clip.Name + "”…", delegate
            {
                _engine.StopPlayback();
                _library.Delete(clip.FilePath);
            });
            if (_closeRequested || IsDisposed) return;
            RefreshClipList();
            UpdateView(_engine.Snapshot());
        }

        private void RenameClip(RecordingClip clip)
        {
            if (_busy || clip == null) return;
            string name = PromptForName(clip.Name);
            if (name == null) return;
            try
            {
                _library.Rename(clip.FilePath, name);
                RefreshClipList();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private string PromptForName(string oldName)
        {
            using (Form dialog = new Form())
            {
                dialog.Text = "给录音改名";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Font = Font;
                dialog.ClientSize = new Size(330, 112);
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                Label label = MakeLabel("名称：", 9F, FontStyle.Regular, Color.FromArgb(50, 60, 75));
                label.Location = new Point(15, 16); label.Size = new Size(55, 24); dialog.Controls.Add(label);
                TextBox input = new TextBox();
                input.Text = oldName; input.Location = new Point(68, 13); input.Size = new Size(245, 24); dialog.Controls.Add(input);
                Button ok = new Button(); ok.Text = "保存"; ok.DialogResult = DialogResult.OK; ok.Location = new Point(157, 64); ok.Size = new Size(74, 28); dialog.Controls.Add(ok);
                Button cancel = new Button(); cancel.Text = "取消"; cancel.DialogResult = DialogResult.Cancel; cancel.Location = new Point(239, 64); cancel.Size = new Size(74, 28); dialog.Controls.Add(cancel);
                dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                dialog.AutoScaleDimensions = new SizeF(96F, 96F);
                dialog.AutoScaleMode = AutoScaleMode.Dpi;
                return dialog.ShowDialog(this) == DialogResult.OK && !String.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null;
            }
        }

        private void RefreshClicked(object sender, EventArgs e)
        {
            if (!_busy)
            {
                RetryPendingLibraryAdd();
                RefreshDevices();
            }
        }

        private void RetryPendingLibraryAdd()
        {
            if (String.IsNullOrEmpty(_pendingLibraryPath)) return;
            try
            {
                _selectedClip = _library.Add(_pendingLibraryPath);
                _pendingLibraryPath = null;
                _libraryAddProblem = false;
                RefreshClipList();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void RefreshDevices()
        {
            AudioDevice oldInput = SelectedDevice(_inputDevices);
            AudioDevice oldOutput = SelectedDevice(_previewDevices);
            List<AudioDevice> inputs = IsComputerCapture ? AudioEngine.LoopbackInputs() : AudioEngine.Inputs();
            List<AudioDevice> outputs = AudioEngine.Outputs().FindAll(delegate(AudioDevice device)
                { return device.Id >= 0 && !AudioEngine.IsVirtualCapture(device.Name); });
            BindDevices(_inputDevices, inputs, oldInput, !IsComputerCapture);
            BindDevices(_previewDevices, outputs, oldOutput, true);
            _hasCable = AudioEngine.FindCableOutput() >= 0;
            _cableLabel.Text = _hasCable ? "虚拟声卡已检测到：可播放给 FIF" : "尚未安装虚拟声卡：仍可录音和本地试听";
            _cableLabel.ForeColor = _hasCable ? Color.FromArgb(29, 120, 91) : Color.FromArgb(171, 93, 43);
            UpdateButtons(_engine.Snapshot());
        }

        private bool IsComputerCapture { get { return _captureSource.SelectedIndex == 0; } }

        private void CaptureSourceChanged(object sender, EventArgs e)
        {
            if (_busy) return;
            _sourceHint.Visible = IsComputerCapture;
            if (IsComputerCapture)
                _sourceHint.Text = "包含浏览器及其他电脑声音；想安静可用耳机。";
            RefreshDevices();
        }

        private void BindDevices(ComboBox box, List<AudioDevice> devices, AudioDevice previous, bool preferRealtek)
        {
            bool hadSelection = previous != null;
            string defaultOutput = null;
            if (box == _previewDevices && !hadSelection)
            {
                try { defaultOutput = AudioEngine.DefaultPlaybackName(); } catch (Exception) { }
            }
            box.BeginUpdate();
            box.Items.Clear();
            int preferredIndex = 0;
            for (int i = 0; i < devices.Count; i++)
            {
                AudioDevice device = devices[i];
                box.Items.Add(device);
                string name = (device.Name ?? String.Empty).ToLowerInvariant();
                if (previous != null && device.IsLoopback == previous.IsLoopback &&
                    (device.IsLoopback ? device.EndpointId == previous.EndpointId : device.Name == previous.Name)) preferredIndex = i;
                if (preferRealtek && !hadSelection && name.IndexOf("realtek") >= 0 &&
                    name.IndexOf("cable") < 0 && name.IndexOf("steam") < 0 && name.IndexOf("virtual") < 0)
                {
                    preferredIndex = i;
                }
            }
            if (!hadSelection && defaultOutput != null)
                for (int i = 0; i < devices.Count; i++)
                    if (defaultOutput.StartsWith(devices[i].Name, StringComparison.OrdinalIgnoreCase)) preferredIndex = i;
            if (box.Items.Count > 0) box.SelectedIndex = preferredIndex;
            box.EndUpdate();
        }

        private async void RecordClicked(object sender, EventArgs e)
        {
            AudioSnapshot snapshot = _engine.Snapshot();
            if (snapshot.Mode == "recording")
            {
                await DoOperation("正在保存录音…", delegate { _engine.StopRecording(); });
            }
            else
            {
                AudioDevice input = SelectedDevice(_inputDevices);
                if (input == null)
                {
                    _statusLabel.Text = "没有可用的录音来源设备，请刷新设备。";
                    return;
                }
                _recordingFileBefore = snapshot.FilePath;
                _awaitingCompletedRecording = true;
                await DoOperation("正在启动录音…", delegate { _engine.StartRecording(input); });
                AudioSnapshot afterStart = _engine.Snapshot();
                if (afterStart.Mode != "recording" && String.Equals(afterStart.FilePath, _recordingFileBefore, StringComparison.OrdinalIgnoreCase))
                    _awaitingCompletedRecording = false;
            }
        }

        private async void FifClicked(object sender, EventArgs e)
        {
            AudioSnapshot snapshot = _engine.Snapshot();
            if (snapshot.Mode == "playing")
            {
                await DoOperation("正在停止播放…", delegate { _engine.StopPlayback(); });
                return;
            }
            if (!_hasCable)
            {
                MessageBox.Show(this, "未检测到 CABLE Input，不能把音频安全地送往 FIF。请先安装虚拟声卡。", "尚未安装虚拟声卡", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_selectedClip == null)
            {
                MessageBox.Show(this, "请先在录音列表中选择一条录音。", "选择录音", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            await PlayClipToFif(_selectedClip);
        }

        private async void PreviewClicked(object sender, EventArgs e)
        {
            AudioSnapshot snapshot = _engine.Snapshot();
            if (snapshot.Mode == "playing")
            {
                await DoOperation("正在停止试听…", delegate { _engine.StopPlayback(); });
            }
            else if (_selectedClip != null) await PreviewClip(_selectedClip);
            else MessageBox.Show(this, "请先在录音列表中选择一条录音。", "选择录音", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async Task PlayClipToFif(RecordingClip clip)
        {
            if (_busy || clip == null) return;
            if (!_hasCable)
            {
                MessageBox.Show(this, "未检测到 CABLE Input，不能把音频安全地送往 FIF。请先安装虚拟声卡。", "尚未安装虚拟声卡", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SelectClip(clip);
            int previewDevice = DeviceId(_previewDevices);
            _playingToFif = true;
            string filePath = clip.FilePath;
            await DoOperation("正在播放“" + clip.Name + "”给 FIF…", delegate
            {
                _engine.StopPlayback();
                _engine.SelectRecording(filePath);
                _engine.StartPlayback(true, previewDevice);
            });
        }

        private async Task PreviewClip(RecordingClip clip)
        {
            if (_busy || clip == null) return;
            SelectClip(clip);
            int previewDevice = DeviceId(_previewDevices);
            _playingToFif = false;
            string filePath = clip.FilePath;
            await DoOperation("正在试听“" + clip.Name + "”…", delegate
            {
                _engine.StopPlayback();
                _engine.SelectRecording(filePath);
                _engine.StartPlayback(false, previewDevice);
            });
        }

        private async void ImportClicked(object sender, EventArgs e)
        {
            if (_busy) return;
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "WAV 音频 (*.wav)|*.wav";
                dialog.Title = "导入 WAV 录音";
                dialog.Multiselect = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string[] selectedFiles = dialog.FileNames;
                List<string> failedNames = new List<string>();
                int[] imported = new int[] { 0 };
                await DoOperation("正在导入 WAV…", delegate
                {
                    for (int i = 0; i < selectedFiles.Length; i++)
                    {
                        try
                        {
                            _engine.LoadWav(selectedFiles[i]);
                            _library.Add(_engine.Snapshot().FilePath);
                            imported[0]++;
                        }
                        catch (Exception) { failedNames.Add(Path.GetFileName(selectedFiles[i])); }
                    }
                });
                if (_closeRequested || IsDisposed) return;
                RefreshClipList();
                if (failedNames.Count > 0)
                    MessageBox.Show(this, "已导入 " + imported[0] + " 条。\r\n以下文件未能导入：\r\n" + String.Join("\r\n", failedNames.ToArray()) +
                        "\r\n\r\n请检查文件是否为支持的 PCM WAV、是否可读取，以及录音目录能否写入。", "导入结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else _statusLabel.Text = "已导入 " + imported[0] + " 条录音。";
            }
        }

        private void FolderClicked(object sender, EventArgs e)
        {
            try
            {
                string directory = _engine.DataDirectory;
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                Process.Start("explorer.exe", "\"" + directory + "\"");
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void InstallClicked(object sender, EventArgs e)
        {
            try
            {
                string readme = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "README.md");
                if (File.Exists(readme)) Process.Start("notepad.exe", "\"" + readme + "\"");
                else Process.Start("https://vb-audio.com/Cable/");
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private async Task DoOperation(string workingText, Action operation)
        {
            if (_busy) return;
            _busy = true;
            _statusLabel.Text = workingText;
            SetControlsEnabled(false);
            try
            {
                await Task.Run(operation);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            finally
            {
                _busy = false;
                AudioSnapshot snapshot = _engine.Snapshot();
                if (_closeRequested) BeginClose();
                else { SetControlsEnabled(true); UpdateView(snapshot); }
            }
        }

        private void TimerTick(object sender, EventArgs e)
        {
            if (_closeFinalizing || _busy) return;
            try { UpdateView(_engine.Snapshot()); }
            catch (Exception ex)
            {
                _timer.Stop();
                ShowError(ex);
            }
        }

        private void UpdateView(AudioSnapshot snapshot)
        {
            if (snapshot == null) return;
            string mode = snapshot.Mode ?? "idle";
            ObserveCompletedRecording(snapshot, mode);
            double seconds = mode == "idle" ? (_selectedClip == null ? 0 : _selectedClip.DurationSeconds) : snapshot.Seconds;
            _timeLabel.Text = TimeText(seconds) + (mode == "playing" && snapshot.TotalSeconds > 0 ? " / " + TimeText(snapshot.TotalSeconds) : String.Empty);
            _statusLabel.Text = mode == "recording" ? (snapshot.CaptureSource == "computer" ? "正在录电脑声音" : "正在录麦克风") : mode == "playing" ? (_playingToFif ? "正在播放给 FIF（不会外放）" : "正在本地试听") : _libraryAddProblem ? "录音已保存，列表更新失败；请点“刷新”重试。" : String.IsNullOrEmpty(snapshot.Error) ? "准备就绪" : "发生错误";
            if (!String.IsNullOrEmpty(snapshot.Error)) _statusLabel.Text = "错误：" + snapshot.Error;
            if (mode == "idle")
                _fileLabel.Text = _selectedClip == null ? "当前录音：尚未录制或导入 WAV" :
                    "已选择：" + _selectedClip.Name + "（" + TimeText(_selectedClip.DurationSeconds) + "）";
            else if (!String.IsNullOrEmpty(snapshot.FilePath)) _fileLabel.Text = "当前录音：" + Path.GetFileName(snapshot.FilePath);
            int peak = (int)Math.Max(0, Math.Min(100, snapshot.Peak * 100F));
            _level.Value = peak;
            if (mode == "playing" && snapshot.TotalSeconds > 0)
            {
                _progress.Maximum = 1000;
                _progress.Value = (int)Math.Max(0, Math.Min(1000, seconds * 1000.0 / snapshot.TotalSeconds));
            }
            else { _progress.Maximum = 1000; _progress.Value = 0; }
            UpdateButtons(snapshot);
        }

        private void ObserveCompletedRecording(AudioSnapshot snapshot, string mode)
        {
            if (_awaitingCompletedRecording && mode != "recording")
            {
                string completedPath = snapshot.FilePath;
                if (!String.IsNullOrEmpty(completedPath) && File.Exists(completedPath) && snapshot.TotalSeconds > 0 &&
                    !String.Equals(completedPath, _recordingFileBefore, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        _selectedClip = _library.Add(completedPath);
                        _awaitingCompletedRecording = false;
                        _pendingLibraryPath = null;
                        _libraryAddProblem = false;
                        RefreshClipList();
                    }
                    catch (Exception)
                    {
                        _awaitingCompletedRecording = false;
                        _pendingLibraryPath = completedPath;
                        if (!_libraryAddProblem)
                        {
                            _libraryAddProblem = true;
                            _statusLabel.Text = "录音已保存，列表更新失败；请点“刷新”重试。";
                        }
                    }
                }
                else if (String.Equals(completedPath, _recordingFileBefore, StringComparison.OrdinalIgnoreCase)) _awaitingCompletedRecording = false;
            }
            _lastMode = mode;
        }

        private void UpdateButtons(AudioSnapshot snapshot)
        {
            if (_busy || _closeRequested) return;
            string mode = snapshot == null ? "idle" : (snapshot.Mode ?? "idle");
            _recordButton.Text = mode == "recording" ? "结束录音" : "开始录音";
            _fifButton.Text = mode == "playing" ? "停止播放" : "播放所选给 FIF";
            _previewButton.Text = mode == "playing" ? "停止播放" : "本地试听（扬声器可能出声）";
            _recordButton.Enabled = mode != "playing" && _inputDevices.Items.Count > 0;
            bool hasClip = _selectedClip != null;
            _fifButton.Enabled = (mode == "playing" || (_hasCable && hasClip)) && mode != "recording";
            _previewButton.Enabled = hasClip && mode != "recording" && _previewDevices.Items.Count > 0;
            _importButton.Enabled = mode == "idle";
            _refreshButton.Enabled = true;
            _captureSource.Enabled = mode == "idle";
            _inputDevices.Enabled = mode == "idle";
            _clipRows.Enabled = mode != "recording";
        }

        private void SetControlsEnabled(bool enabled)
        {
            _recordButton.Enabled = enabled;
            _fifButton.Enabled = enabled && _hasCable;
            _previewButton.Enabled = enabled;
            _refreshButton.Enabled = enabled;
            _folderButton.Enabled = enabled;
            _importButton.Enabled = enabled;
            _installButton.Enabled = enabled;
            _settingsLink.Enabled = enabled;
            _captureSource.Enabled = enabled;
            _inputDevices.Enabled = enabled;
            _previewDevices.Enabled = enabled;
            _clipRows.Enabled = enabled;
        }

        private void SettingsClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            _settingsVisible = !_settingsVisible;
            _settingsPanel.Visible = _settingsVisible;
            _settingsLink.Text = _settingsVisible ? "设备与设置  ▴" : "设备与设置  ▾";
            float scale = ClientSize.Width / 430F;
            int captionY = _settingsVisible ? 563 : 410;
            int panelY = _settingsVisible ? 587 : 434;
            int panelHeight = _settingsVisible ? 90 : 154;
            _libraryCaption.Location = ScalePoint(21, captionY, scale);
            _libraryCaption.Size = ScaleSize(300, 22, scale);
            _libraryPanel.Location = ScalePoint(18, panelY, scale);
            _libraryPanel.Size = ScaleSize(394, panelHeight, scale);

            int desiredClientHeight = (int)Math.Ceiling((_settingsVisible ? 685 : 610) * scale);
            int chromeHeight = Height - ClientSize.Height;
            int availableClientHeight = Screen.FromControl(this).WorkingArea.Height - chromeHeight - 16;
            ClientSize = new Size((int)Math.Round(430 * scale), Math.Max(360, Math.Min(desiredClientHeight, availableClientHeight)));

            int availableListHeight = ClientSize.Height - _libraryPanel.Top - (int)Math.Ceiling(12 * scale);
            int twoRows = (int)Math.Ceiling(84 * scale);
            if (availableListHeight >= twoRows) _libraryPanel.Height = Math.Min(_libraryPanel.Height, availableListHeight);
            else _libraryPanel.Height = Math.Max(20, availableListHeight);
        }

        private static Point ScalePoint(int x, int y, float scale)
        {
            return new Point((int)Math.Round(x * scale), (int)Math.Round(y * scale));
        }

        private static Size ScaleSize(int width, int height, float scale)
        {
            return new Size((int)Math.Round(width * scale), (int)Math.Round(height * scale));
        }

        private void FormIsClosing(object sender, FormClosingEventArgs e)
        {
            if (_closingInternally) return;
            e.Cancel = true;
            _closeRequested = true;
            if (!_busy) BeginClose();
            else _statusLabel.Text = "正在完成当前操作后关闭…";
        }

        private async void BeginClose()
        {
            if (_closeFinalizing) return;
            _closeFinalizing = true;
            _timer.Stop();
            SetControlsEnabled(false);
            try
            {
                await Task.Run(delegate
                {
                    AudioSnapshot snapshot = _engine.Snapshot();
                    if (snapshot != null && snapshot.Mode == "recording") _engine.StopRecording();
                    else if (snapshot != null && snapshot.Mode == "playing") _engine.StopPlayback();
                    _engine.Dispose();
                });
            }
            catch (Exception ex)
            {
                _closeFinalizing = false;
                _closeRequested = false;
                SetControlsEnabled(true);
                _timer.Start();
                ShowError(ex);
                UpdateView(_engine.Snapshot());
                return;
            }
            _closingInternally = true;
            BeginInvoke(new MethodInvoker(Close));
        }

        private static int DeviceId(ComboBox box)
        {
            AudioDevice device = SelectedDevice(box);
            return device == null ? -1 : device.Id;
        }

        private static AudioDevice SelectedDevice(ComboBox box)
        {
            return box == null ? null : box.SelectedItem as AudioDevice;
        }

        private static string TimeText(double seconds)
        {
            if (seconds < 0) seconds = 0;
            TimeSpan span = TimeSpan.FromSeconds(seconds);
            return String.Format("{0:00}:{1:00}", (int)span.TotalMinutes, span.Seconds);
        }

        private void ShowError(Exception ex)
        {
            if (IsDisposed || Disposing) return;
            _statusLabel.Text = "操作失败：" + ex.Message;
            MessageBox.Show(this, "操作失败：\r\n" + ex.Message, "FIF 录音助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
            label.ForeColor = color;
            return label;
        }

        private static Button MakeButton(string text, Color color)
        {
            Button button = new Button();
            button.Text = text;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = color;
            button.ForeColor = Color.White;
            button.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
            button.Cursor = Cursors.Hand;
            return button;
        }

        private static Button MakeSmallButton(string text, Color color)
        {
            Button button = MakeButton(text, color);
            button.Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold, GraphicsUnit.Point);
            return button;
        }

        private void DragStart(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _dragging = true; _dragStart = e.Location; }
        }

        private void DragMove(object sender, MouseEventArgs e)
        {
            if (_dragging) Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y);
        }

        private void DragEnd(object sender, MouseEventArgs e) { _dragging = false; }
    }
}
