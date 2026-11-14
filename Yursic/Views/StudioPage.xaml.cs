using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;
using Yursic.Audio;
using Yursic.Helpers;
using Yursic.Models;
using Yursic.Plugins;

namespace Yursic.Views
{
    /// <summary>The multitrack studio: transport, tracks/timeline, and the Mixer / Effects / Sounds panel.</summary>
    public sealed partial class StudioPage : Page, IBackHandler
    {
        private const double LaneHeight = 100;
        private const double LaneGap = 2;
        private const double RowHeight = LaneHeight + LaneGap;

        private AudioEngine Engine => Services.Engine;

        private Project _project;
        private Track _selectedTrack;
        private Clip _selectedClip;
        private Clip _moveClip; // non-null while in controller "move mode"
        private double _pps = 60; // pixels per second
        private double _timelineSeconds = 120;

        private readonly Dictionary<Clip, Button> _clipButtons = new Dictionary<Clip, Button>();
        private readonly List<Canvas> _laneCanvases = new List<Canvas>();
        private readonly Stack<KeyValuePair<string, Action>> _undo = new Stack<KeyValuePair<string, Action>>();

        private readonly DispatcherTimer _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        private readonly DispatcherTimer _autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };

        private Track _recTrack;
        private double _recStart;
        private Rectangle _recRect;
        private int _tabIndex = 2;
        private bool _ready;

        public StudioPage()
        {
            InitializeComponent();
            if (Services.IsXbox) Root.Margin = new Thickness(48, 27, 48, 0);
            _uiTimer.Tick += UiTimer_Tick;
            _autosave.Tick += async (s, e) => { if (!Engine.IsRecording) await SaveAsync(); };
            AddEffectMenu.Opening += (s, e) => BuildEffectMenu();
        }

        // ------------------------------------------------------------------ lifecycle

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            var id = e.Parameter as string;
            ShowBusy("Opening project…");
            var warnings = new List<string>();
            try
            {
                await Engine.InitAsync();
                _project = await Services.Projects.LoadAsync(id, Engine.SampleRate, Services.Plugins, warnings);
            }
            catch (Exception ex)
            {
                HideBusy();
                await Dialogs.InfoAsync("Couldn't open the studio", ex.Message);
                if (Frame.CanGoBack) Frame.GoBack();
                return;
            }

            Services.Projects.Current = _project;
            Engine.PositionSeconds = 0;
            Engine.Mixer.UpdateSnapshot(_project);

            TrackHeaders.ItemsSource = _project.Tracks;
            MixerStrips.ItemsSource = _project.Tracks;
            ProjectNameButton.Content = _project.Name;
            BpmSlider.Value = _project.Bpm;
            MasterSlider.Value = _project.MasterVolume;
            MetronomeToggle.IsChecked = Engine.Mixer.MetronomeOn;
            UpdateBpmText();
            SelectTrack(_project.Tracks.FirstOrDefault());

            _ready = true;
            RebuildTimeline();
            await LoadSoundsAsync();
            ShowTab(_tabIndex);
            HideBusy();

            _uiTimer.Start();
            _autosave.Start();

            if (!Engine.HasMicrophone)
                SetStatus(Engine.MicrophoneError ?? "No microphone: you can still build songs from loops and USB audio.");
            if (warnings.Count > 0)
                await Dialogs.InfoAsync("Some parts couldn't be loaded", string.Join("\n", warnings));

            PlayButton.Focus(FocusState.Keyboard);
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            _uiTimer.Stop();
            _autosave.Stop();
            if (Engine.IsRecording) await StopRecordingAsync();
            Engine.Pause();
            var p = _project;
            Engine.Mixer.UpdateSnapshot(null);
            Services.Projects.Current = null;
            if (p != null && !_savedOnLeave)
            {
                try { await Services.Projects.SaveAsync(p); } catch { }
            }
            SampleCache.Clear();
        }

        private bool _savedOnLeave, _leaving;

        public bool HandleBack()
        {
            if (_moveClip != null) { EndMoveMode(); return true; }
            _ = LeaveAsync();
            return true;
        }

        /// <summary>Stops audio and saves before going back, so the project list never reads a half-written file.</summary>
        private async Task LeaveAsync()
        {
            if (_leaving) return;
            _leaving = true;
            if (Engine.IsRecording) await StopRecordingAsync();
            Engine.Pause();
            if (_project != null)
            {
                ShowBusy("Saving…");
                await SaveAsync();
                _savedOnLeave = true;
            }
            if (Frame.CanGoBack) Frame.GoBack();
        }

        // ------------------------------------------------------------------ input

        private void Page_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (!_ready) return;
            if (FocusManager.GetFocusedElement() is TextBox) return;

            bool ctrl = Window.Current.CoreWindow.GetKeyState(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

            if (_moveClip != null)
            {
                switch (e.Key)
                {
                    case VirtualKey.Left:
                    case VirtualKey.GamepadDPadLeft:
                    case VirtualKey.GamepadLeftThumbstickLeft:
                        NudgeClip(_moveClip, -1); e.Handled = true; return;
                    case VirtualKey.Right:
                    case VirtualKey.GamepadDPadRight:
                    case VirtualKey.GamepadLeftThumbstickRight:
                        NudgeClip(_moveClip, 1); e.Handled = true; return;
                    case VirtualKey.Up:
                    case VirtualKey.GamepadDPadUp:
                    case VirtualKey.GamepadLeftThumbstickUp:
                        MoveClipToTrack(_moveClip, -1); e.Handled = true; return;
                    case VirtualKey.Down:
                    case VirtualKey.GamepadDPadDown:
                    case VirtualKey.GamepadLeftThumbstickDown:
                        MoveClipToTrack(_moveClip, 1); e.Handled = true; return;
                    case VirtualKey.Enter:
                    case VirtualKey.Escape:
                    case VirtualKey.GamepadA:
                    case VirtualKey.GamepadB:
                        EndMoveMode(); e.Handled = true; return;
                }
            }

            switch (e.Key)
            {
                case VirtualKey.GamepadX:
                case VirtualKey.Space:
                    TogglePlay(); e.Handled = true; break;
                case VirtualKey.GamepadY:
                    _ = ToggleRecordAsync(); e.Handled = true; break;
                case VirtualKey.R when !ctrl:
                    _ = ToggleRecordAsync(); e.Handled = true; break;
                case VirtualKey.GamepadLeftShoulder:
                case VirtualKey.PageUp:
                    JumpBars(-1); e.Handled = true; break;
                case VirtualKey.GamepadRightShoulder:
                case VirtualKey.PageDown:
                    JumpBars(1); e.Handled = true; break;
                case VirtualKey.GamepadLeftTrigger:
                case VirtualKey.Subtract:
                    Zoom(1 / 1.4); e.Handled = true; break;
                case VirtualKey.GamepadRightTrigger:
                case VirtualKey.Add:
                    Zoom(1.4); e.Handled = true; break;
                case VirtualKey.GamepadView:
                    ShowTab((_tabIndex + 1) % 3); e.Handled = true; break;
                case VirtualKey.Home:
                    Seek(0); e.Handled = true; break;
                case VirtualKey.Delete:
                    if (_selectedClip != null) { DeleteClip(_selectedClip); e.Handled = true; }
                    break;
                case VirtualKey.S when ctrl:
                    _ = SaveAsync(); e.Handled = true; break;
                case VirtualKey.Z when ctrl:
                    Undo(); e.Handled = true; break;
            }
        }

        // ------------------------------------------------------------------ transport

        private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

        private void TogglePlay()
        {
            if (Engine.IsRecording) { _ = StopRecordingAsync(); return; }
            if (Engine.IsPlaying) Engine.Pause();
            else Engine.Play();
            UpdateTransportVisuals();
        }

        private void Record_Click(object sender, RoutedEventArgs e) => _ = ToggleRecordAsync();

        private async Task ToggleRecordAsync()
        {
            if (Engine.IsRecording) { await StopRecordingAsync(); return; }
            if (!Engine.HasMicrophone)
            {
                await Dialogs.InfoAsync("No microphone", (Engine.MicrophoneError ?? "Connect a headset or USB microphone.") +
                    "\n\nYou can still import audio from a USB drive in Sounds.");
                return;
            }
            var track = _project.Tracks.FirstOrDefault(t => t.Armed) ?? _selectedTrack ?? _project.AddTrack("Vocals");
            foreach (var t in _project.Tracks) t.Armed = t == track;
            SelectTrack(track);

            _recTrack = track;
            _recStart = Engine.PositionSeconds;
            int laneIndex = _project.Tracks.IndexOf(track);
            if (laneIndex >= 0 && laneIndex < _laneCanvases.Count)
            {
                _recRect = new Rectangle
                {
                    Height = LaneHeight - 4,
                    Width = 1,
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 255, 59, 92)),
                    RadiusX = 6,
                    RadiusY = 6
                };
                Canvas.SetLeft(_recRect, _recStart * _pps);
                Canvas.SetTop(_recRect, 2);
                _laneCanvases[laneIndex].Children.Add(_recRect);
            }
            Engine.StartRecording();
            SetStatus($"Recording on {track.Name}… press Y or Record again to stop.");
            UpdateTransportVisuals();
        }

        private async Task StopRecordingAsync()
        {
            var data = Engine.StopRecording();
            UpdateTransportVisuals();
            var track = _recTrack;
            _recTrack = null;
            if (_recRect != null && _recRect.Parent is Canvas c) c.Children.Remove(_recRect);
            _recRect = null;

            int sr = Engine.SampleRate;
            if (track == null || data.Length < sr / 5) { SetStatus("Recording was too short."); return; }

            try
            {
                string fileName = $"rec_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
                await AudioImporter.SaveWavAsync(_project.AudioFolder, fileName, data, sr);
                var buffer = new SampleBuffer(data, sr);
                var stored = await _project.AudioFolder.GetFileAsync(fileName);
                SampleCache.Put(stored.Path + "@" + sr, buffer);

                int take = track.Clips.Count + 1;
                var clip = new Clip
                {
                    FileName = fileName,
                    Name = $"{track.Name} take {take}",
                    Buffer = buffer,
                    StartSeconds = _recStart,
                    LengthSeconds = buffer.Seconds
                };
                track.Clips.Add(clip);
                PushUndo("recording", () => track.Clips.Remove(clip));
                AfterEdit();
                SelectClip(clip);
                SetStatus($"Recorded {Format.Time(buffer.Seconds)} on {track.Name}.");
                await SaveAsync();
            }
            catch (Exception ex)
            {
                await Dialogs.InfoAsync("Couldn't save the recording", ex.Message);
            }
        }

        private void ToStart_Click(object sender, RoutedEventArgs e) => Seek(0);
        private void BarBack_Click(object sender, RoutedEventArgs e) => JumpBars(-1);
        private void BarForward_Click(object sender, RoutedEventArgs e) => JumpBars(1);

        private double BarSeconds => 60.0 / _project.Bpm * 4;
        private double BeatSeconds => 60.0 / _project.Bpm;

        private void JumpBars(int dir)
        {
            double bar = BarSeconds;
            double pos = Engine.PositionSeconds;
            double target = dir < 0
                ? Math.Floor((pos - 0.05) / bar) * bar
                : (Math.Floor(pos / bar + 0.001) + 1) * bar;
            Seek(target);
        }

        private void Seek(double seconds)
        {
            if (Engine.IsRecording) return;
            Engine.PositionSeconds = Math.Max(0, seconds);
            ScrollToPlayhead(false);
        }

        private void UpdateTransportVisuals()
        {
            PlayButton.Content = Engine.IsPlaying && !Engine.IsRecording ? "" : "";
            RecordButton.Background = Engine.IsRecording
                ? (Brush)Application.Current.Resources["YRecord"]
                : (Brush)Application.Current.Resources["YSurface2"];
            RecordDot.Fill = Engine.IsRecording
                ? new SolidColorBrush(Windows.UI.Colors.White)
                : (Brush)Application.Current.Resources["YRecord"];
        }

        private void Metronome_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            Engine.Mixer.MetronomeOn = MetronomeToggle.IsChecked == true;
        }

        private void BpmSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_ready || _project == null) return;
            _project.Bpm = Math.Round(e.NewValue);
            UpdateBpmText();
            RebuildTimeline();
        }

        private void BpmDown_Click(object sender, RoutedEventArgs e) => BpmSlider.Value = Math.Max(40, BpmSlider.Value - 1);
        private void BpmUp_Click(object sender, RoutedEventArgs e) => BpmSlider.Value = Math.Min(240, BpmSlider.Value + 1);
        private void UpdateBpmText() => BpmButton.Content = $"{_project.Bpm:0} BPM";

        private void Master_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_project == null) return;
            _project.MasterVolume = e.NewValue;
            MasterDb.Text = new GainToDbConverter().Convert(e.NewValue, null, null, null) as string;
        }

        // ------------------------------------------------------------------ periodic UI

        private void UiTimer_Tick(object sender, object e)
        {
            if (_project == null) return;
            double pos = Engine.PositionSeconds;
            TimeText.Text = Format.Time(pos);
            BarText.Text = "Bar " + Format.BarBeat(pos, _project.Bpm);
            double x = pos * _pps;
            Canvas.SetLeft(Playhead, x);

            foreach (var t in _project.Tracks) t.PollMeter();
            MasterMeterScale.ScaleY = Math.Min(1, Engine.Mixer.MasterMeter);

            if (Engine.IsRecording && _recRect != null)
            {
                _recRect.Width = Math.Max(1, (pos - _recStart) * _pps);
                RecordButton.Opacity = (DateTime.Now.Millisecond / 500) % 2 == 0 ? 1 : 0.6;
            }
            else RecordButton.Opacity = 1;

            if (pos > _timelineSeconds - 20) RebuildTimeline();
            if (Engine.IsPlaying) ScrollToPlayhead(true);
        }

        private void ScrollToPlayhead(bool onlyIfOutside)
        {
            double x = Engine.PositionSeconds * _pps;
            double left = HScroll.HorizontalOffset, width = HScroll.ViewportWidth;
            if (width <= 0) return;
            if (!onlyIfOutside || x < left || x > left + width - 80)
                HScroll.ChangeView(Math.Max(0, x - width * 0.25), null, null, onlyIfOutside);
        }

        private void HScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) =>
            RulerScroll.ChangeView(HScroll.HorizontalOffset, null, null, true);

        private void SetStatus(string text) => StatusText.Text = text ?? "";

        private void ShowBusy(string text)
        {
            BusyText.Text = text;
            Busy.Visibility = Visibility.Visible;
        }

        private void HideBusy() => Busy.Visibility = Visibility.Collapsed;

        // ------------------------------------------------------------------ timeline drawing

        private void Zoom(double factor)
        {
            _pps = Math.Max(8, Math.Min(480, _pps * factor));
            RebuildTimeline();
            ScrollToPlayhead(false);
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(1.4);
        private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(1 / 1.4);

        /// <summary>Redraws ruler, grid lines and every lane/clip. Cheap enough to call after each edit.</summary>
        private void RebuildTimeline()
        {
            if (_project == null) return;
            bool hadFocus = _selectedClip != null && _clipButtons.TryGetValue(_selectedClip, out var oldBtn) &&
                            FocusManager.GetFocusedElement() == oldBtn;

            _timelineSeconds = Math.Max(120, Math.Max(_project.LengthSeconds, Engine.PositionSeconds) + 60);
            double width = _timelineSeconds * _pps;
            double height = Math.Max(1, _project.Tracks.Count) * RowHeight;

            DrawRuler(width);
            DrawGrid(width, height);

            Lanes.Children.Clear();
            _laneCanvases.Clear();
            _clipButtons.Clear();
            Lanes.Width = width;

            foreach (var track in _project.Tracks)
            {
                var lane = new Canvas
                {
                    Height = LaneHeight,
                    Width = width,
                    Margin = new Thickness(0, 0, 0, LaneGap),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(track.IsSelected ? (byte)18 : (byte)8, 255, 255, 255))
                };
                foreach (var clip in track.Clips)
                {
                    var btn = MakeClipButton(track, clip);
                    lane.Children.Add(btn);
                    _clipButtons[clip] = btn;
                }
                if (_recRect != null && track == _recTrack)
                    lane.Children.Add(_recRect);
                Lanes.Children.Add(lane);
                _laneCanvases.Add(lane);
            }

            Overlay.Width = width;
            Overlay.Height = height;
            Playhead.Height = height;
            HintText.Visibility = _project.Tracks.Any(t => t.Clips.Count > 0) ? Visibility.Collapsed : Visibility.Visible;

            if (hadFocus && _clipButtons.TryGetValue(_selectedClip, out var newBtn))
                newBtn.Focus(FocusState.Keyboard);
        }

        private void DrawRuler(double width)
        {
            Ruler.Children.Clear();
            Ruler.Width = width;
            double barPx = BarSeconds * _pps;
            int every = 1;
            while (barPx * every < 48) every *= 2;
            var dim = (Brush)Application.Current.Resources["YTextDim"];
            var line = (Brush)Application.Current.Resources["YLine"];
            int bars = (int)(_timelineSeconds / BarSeconds) + 1;
            for (int b = 0; b < bars; b += every)
            {
                double x = b * barPx;
                var r = new Rectangle { Width = 1, Height = 12, Fill = line };
                Canvas.SetLeft(r, x);
                Canvas.SetTop(r, 20);
                Ruler.Children.Add(r);
                var t = new TextBlock { Text = (b + 1).ToString(), FontSize = 12, Foreground = dim };
                Canvas.SetLeft(t, x + 4);
                Canvas.SetTop(t, 4);
                Ruler.Children.Add(t);
            }
        }

        private void DrawGrid(double width, double height)
        {
            GridLines.Children.Clear();
            GridLines.Width = width;
            GridLines.Height = height;
            double beatPx = BeatSeconds * _pps;
            bool drawBeats = beatPx >= 10;
            var barBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255));
            var beatBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(16, 255, 255, 255));
            int beats = (int)(_timelineSeconds / BeatSeconds) + 1;
            for (int i = 0; i < beats; i++)
            {
                bool bar = i % 4 == 0;
                if (!bar && !drawBeats) continue;
                if (bar && !drawBeats && (i / 4) % Math.Max(1, (int)Math.Ceiling(12 / (beatPx * 4))) != 0) continue;
                var r = new Rectangle { Width = 1, Height = height, Fill = bar ? barBrush : beatBrush };
                Canvas.SetLeft(r, i * beatPx);
                GridLines.Children.Add(r);
            }
        }

        private Button MakeClipButton(Track track, Clip clip)
        {
            double w = Math.Max(6, clip.LengthSeconds * _pps);
            double h = LaneHeight - 4;
            var content = new Grid { Width = w - 4, Height = h - 4 };
            content.Children.Add(MakeWaveform(clip, w - 4, h - 22));
            content.Children.Add(new TextBlock
            {
                Text = clip.Name ?? "",
                FontSize = 12,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(6, 2, 4, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Top
            });

            var btn = new Button
            {
                Width = w,
                Height = h,
                Padding = new Thickness(2),
                Content = content,
                Background = track.ClipBrush,
                BorderBrush = clip.IsSelected ? new SolidColorBrush(Windows.UI.Colors.White) : track.Brush,
                BorderThickness = new Thickness(clip.IsSelected ? 2 : 1),
                CornerRadius = new CornerRadius(6),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Tag = clip,
                ContextFlyout = MakeClipMenu(track, clip)
            };
            if (clip == _moveClip)
                btn.BorderBrush = (Brush)Application.Current.Resources["YAccent2"];
            ToolTipService.SetToolTip(btn, $"{clip.Name} · {Format.Time(clip.LengthSeconds)}");
            btn.Click += (s, e) =>
            {
                if (_selectedClip == clip) BeginMoveMode(clip);
                else SelectClip(clip);
            };
            Canvas.SetLeft(btn, clip.StartSeconds * _pps);
            Canvas.SetTop(btn, 2);
            return btn;
        }

        private UIElement MakeWaveform(Clip clip, double width, double height)
        {
            var poly = new Polygon
            {
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(170, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 2),
                IsHitTestVisible = false
            };
            var buf = clip.Buffer;
            if (buf == null || width < 2) return poly;

            var peaks = buf.Peaks;
            double framesPerPx = buf.SampleRate / _pps;
            double startFrame = clip.OffsetSeconds * buf.SampleRate;
            int step = width > 3000 ? 3 : width > 1500 ? 2 : 1;
            double mid = height / 2;
            var top = new List<Point>();
            for (int x = 0; x < width; x += step)
            {
                long b0 = (long)((startFrame + x * framesPerPx) / SampleBuffer.PeakResolution);
                long b1 = (long)((startFrame + (x + step) * framesPerPx) / SampleBuffer.PeakResolution);
                if (b1 <= b0) b1 = b0 + 1;
                float m = 0;
                for (long b = b0; b < b1 && b < peaks.Length; b++)
                    if (peaks[b] > m) m = peaks[b];
                double amp = Math.Max(0.5, Math.Min(1, m) * mid);
                top.Add(new Point(x, mid - amp));
            }
            var pts = new PointCollection();
            foreach (var p in top) pts.Add(p);
            for (int i = top.Count - 1; i >= 0; i--) pts.Add(new Point(top[i].X, mid + (mid - top[i].Y)));
            poly.Points = pts;
            poly.Height = height;
            return poly;
        }

        private void Ruler_Tapped(object sender, TappedRoutedEventArgs e) =>
            Seek(e.GetPosition(Ruler).X / _pps);

        private void Lanes_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (!(e.OriginalSource is Canvas)) return; // taps on clips are handled by the clip
            var p = e.GetPosition(Lanes);
            Seek(p.X / _pps);
            int idx = (int)(p.Y / RowHeight);
            if (idx >= 0 && idx < _project.Tracks.Count) SelectTrack(_project.Tracks[idx]);
        }

        // ------------------------------------------------------------------ selection

        private void SelectTrack(Track track)
        {
            if (_project == null) return;
            foreach (var t in _project.Tracks) t.IsSelected = t == track;
            bool changed = _selectedTrack != track;
            _selectedTrack = track;
            if (changed && _ready) RebuildTimeline();
            RefreshEffectsPanel();
        }

        private void SelectClip(Clip clip)
        {
            foreach (var t in _project.Tracks)
                foreach (var c in t.Clips) c.IsSelected = c == clip;
            _selectedClip = clip;
            var owner = OwnerOf(clip);
            if (owner != null && owner != _selectedTrack)
            {
                foreach (var t in _project.Tracks) t.IsSelected = t == owner;
                _selectedTrack = owner;
                RefreshEffectsPanel();
            }
            RebuildTimeline();
            if (clip != null && _clipButtons.TryGetValue(clip, out var b)) b.Focus(FocusState.Keyboard);
            if (clip != null) SetStatus($"{clip.Name} selected. Press A again to move it, ☰ for more.");
        }

        private Track OwnerOf(Clip clip) => clip == null ? null : _project.Tracks.FirstOrDefault(t => t.Clips.Contains(clip));

        private void BeginMoveMode(Clip clip)
        {
            _moveClip = clip;
            RebuildTimeline();
            if (_clipButtons.TryGetValue(clip, out var b)) b.Focus(FocusState.Keyboard);
            SetStatus("Move mode: D-pad ←/→ moves by a beat, ↑/↓ changes track. A or B to finish.");
        }

        private void EndMoveMode()
        {
            var c = _moveClip;
            _moveClip = null;
            RebuildTimeline();
            if (c != null && _clipButtons.TryGetValue(c, out var b)) b.Focus(FocusState.Keyboard);
            SetStatus("");
        }

        // ------------------------------------------------------------------ clip editing

        private MenuFlyout MakeClipMenu(Track track, Clip clip)
        {
            var m = new MenuFlyout();
            void Add(string text, string glyph, Action action)
            {
                var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
                item.Click += (s, e) => action();
                m.Items.Add(item);
            }
            Add("Move", "", () => { SelectClip(clip); BeginMoveMode(clip); });
            Add("Move to playhead", "", () => MoveClipTo(clip, Engine.PositionSeconds));
            Add("Split at playhead", "", () => SplitClip(clip, Engine.PositionSeconds));
            Add("Duplicate", "", () => DuplicateClip(clip));
            m.Items.Add(new MenuFlyoutSeparator());
            Add("Trim start to playhead", "", () => TrimStart(clip, Engine.PositionSeconds));
            Add("Trim end to playhead", "", () => TrimEnd(clip, Engine.PositionSeconds));
            Add("Louder (+3 dB)", "", () => ChangeGain(clip, 1.4125));
            Add("Quieter (−3 dB)", "", () => ChangeGain(clip, 1 / 1.4125));
            m.Items.Add(new MenuFlyoutSeparator());
            Add("Delete", "", () => DeleteClip(clip));
            return m;
        }

        private void PushUndo(string label, Action undo)
        {
            _undo.Push(new KeyValuePair<string, Action>(label, undo));
            if (_undo.Count > 100)
            {
                var keep = _undo.Take(100).Reverse().ToList();
                _undo.Clear();
                foreach (var k in keep) _undo.Push(k);
            }
        }

        private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

        private void Undo()
        {
            if (_undo.Count == 0) { SetStatus("Nothing to undo."); return; }
            var u = _undo.Pop();
            u.Value();
            AfterEdit();
            SetStatus("Undid " + u.Key + ".");
        }

        /// <summary>Call after any structural edit so the audio thread and the UI catch up.</summary>
        private void AfterEdit()
        {
            Engine.Mixer.UpdateSnapshot(_project);
            if (_selectedClip != null && OwnerOf(_selectedClip) == null) _selectedClip = null;
            if (_moveClip != null && OwnerOf(_moveClip) == null) _moveClip = null;
            RebuildTimeline();
        }

        private void NudgeClip(Clip clip, int dir)
        {
            double beat = BeatSeconds;
            double old = clip.StartSeconds;
            double snapped = Math.Round(old / beat) * beat;
            double target = Math.Abs(snapped - old) > 0.001 && Math.Sign(snapped - old) == dir ? snapped : snapped + dir * beat;
            clip.StartSeconds = Math.Max(0, target);
            PushUndo("move", () => clip.StartSeconds = old);
            AfterEdit();
        }

        private void MoveClipTo(Clip clip, double start)
        {
            double old = clip.StartSeconds;
            clip.StartSeconds = start;
            PushUndo("move", () => clip.StartSeconds = old);
            AfterEdit();
        }

        private void MoveClipToTrack(Clip clip, int dir)
        {
            var from = OwnerOf(clip);
            if (from == null) return;
            int idx = _project.Tracks.IndexOf(from) + dir;
            if (idx < 0 || idx >= _project.Tracks.Count) return;
            var to = _project.Tracks[idx];
            from.Clips.Remove(clip);
            to.Clips.Add(clip);
            PushUndo("move", () => { to.Clips.Remove(clip); from.Clips.Add(clip); });
            foreach (var t in _project.Tracks) t.IsSelected = t == to;
            _selectedTrack = to;
            RefreshEffectsPanel();
            AfterEdit();
            if (_clipButtons.TryGetValue(clip, out var b)) b.Focus(FocusState.Keyboard);
        }

        private void SplitClip(Clip clip, double at)
        {
            if (at <= clip.StartSeconds + 0.01 || at >= clip.EndSeconds - 0.01)
            {
                SetStatus("Put the playhead inside the clip to split it.");
                return;
            }
            var track = OwnerOf(clip);
            double oldLen = clip.LengthSeconds;
            var right = clip.CloneAt(at);
            right.OffsetSeconds = clip.OffsetSeconds + (at - clip.StartSeconds);
            right.LengthSeconds = clip.EndSeconds - at;
            clip.LengthSeconds = at - clip.StartSeconds;
            track.Clips.Add(right);
            PushUndo("split", () => { track.Clips.Remove(right); clip.LengthSeconds = oldLen; });
            AfterEdit();
        }

        private void DuplicateClip(Clip clip)
        {
            var track = OwnerOf(clip);
            var copy = clip.CloneAt(clip.EndSeconds);
            track.Clips.Add(copy);
            PushUndo("duplicate", () => track.Clips.Remove(copy));
            AfterEdit();
            SelectClip(copy);
        }

        private void TrimStart(Clip clip, double at)
        {
            if (at <= clip.StartSeconds || at >= clip.EndSeconds - 0.01) { SetStatus("Put the playhead inside the clip to trim."); return; }
            double s = clip.StartSeconds, o = clip.OffsetSeconds, l = clip.LengthSeconds;
            double d = at - s;
            clip.OffsetSeconds = o + d;
            clip.LengthSeconds = l - d;
            clip.StartSeconds = at;
            PushUndo("trim", () => { clip.StartSeconds = s; clip.OffsetSeconds = o; clip.LengthSeconds = l; });
            AfterEdit();
        }

        private void TrimEnd(Clip clip, double at)
        {
            if (at <= clip.StartSeconds + 0.01 || at >= clip.EndSeconds) { SetStatus("Put the playhead inside the clip to trim."); return; }
            double l = clip.LengthSeconds;
            clip.LengthSeconds = at - clip.StartSeconds;
            PushUndo("trim", () => clip.LengthSeconds = l);
            AfterEdit();
        }

        private void ChangeGain(Clip clip, double factor)
        {
            double g = clip.Gain;
            clip.Gain = Math.Max(0.05, Math.Min(4, g * factor));
            PushUndo("gain", () => clip.Gain = g);
            SetStatus($"{clip.Name}: {20 * Math.Log10(clip.Gain):+0.0;-0.0} dB");
        }

        private void DeleteClip(Clip clip)
        {
            var track = OwnerOf(clip);
            if (track == null) return;
            int idx = track.Clips.IndexOf(clip);
            track.Clips.Remove(clip);
            PushUndo("delete", () => track.Clips.Insert(Math.Min(idx, track.Clips.Count), clip));
            if (_selectedClip == clip) _selectedClip = null;
            if (_moveClip == clip) _moveClip = null;
            AfterEdit();
        }

        // ------------------------------------------------------------------ tracks

        private void AddTrack_Click(object sender, RoutedEventArgs e)
        {
            var t = _project.AddTrack();
            PushUndo("add track", () => _project.Tracks.Remove(t));
            SelectTrack(t);
            AfterEdit();
        }

        private static Track TagTrack(object sender) => (sender as FrameworkElement)?.Tag as Track;

        private void TrackName_Click(object sender, RoutedEventArgs e)
        {
            var t = TagTrack(sender);
            if (t != null) SelectTrack(t);
        }

        private void TrackEffects_Click(object sender, RoutedEventArgs e)
        {
            var t = TagTrack(sender);
            if (t == null) return;
            SelectTrack(t);
            ShowTab(1);
        }

        private async void TrackRename_Click(object sender, RoutedEventArgs e)
        {
            var t = TagTrack(sender);
            if (t == null) return;
            var name = await Dialogs.PromptAsync("Rename track", "Name", t.Name, "Rename");
            if (!string.IsNullOrWhiteSpace(name)) t.Name = name.Trim();
        }

        private void TrackColor_Click(object sender, RoutedEventArgs e)
        {
            var t = TagTrack(sender);
            if (t == null) return;
            int i = Array.IndexOf(Project.TrackColors, t.ColorHex);
            t.ColorHex = Project.TrackColors[(i + 1) % Project.TrackColors.Length];
            RebuildTimeline();
        }

        private async void TrackDelete_Click(object sender, RoutedEventArgs e)
        {
            var t = TagTrack(sender);
            if (t == null) return;
            if (t.Clips.Count > 0 && !await Dialogs.ConfirmAsync("Delete track?", $"\"{t.Name}\" and its {t.Clips.Count} clip(s) will be removed. You can undo this.", "Delete"))
                return;
            int idx = _project.Tracks.IndexOf(t);
            _project.Tracks.Remove(t);
            PushUndo("delete track", () => _project.Tracks.Insert(Math.Min(idx, _project.Tracks.Count), t));
            if (_selectedTrack == t) SelectTrack(_project.Tracks.FirstOrDefault());
            AfterEdit();
        }

        private void Arm_Checked(object sender, RoutedEventArgs e)
        {
            var t = TagTrack(sender);
            if (t == null || _project == null) return;
            foreach (var other in _project.Tracks)
                if (other != t) other.Armed = false;
            SelectTrack(t);
        }

        // ------------------------------------------------------------------ bottom panel

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            if (sender == TabMixer) ShowTab(0);
            else if (sender == TabEffects) ShowTab(1);
            else ShowTab(2);
        }

        private void ShowTab(int index)
        {
            _tabIndex = index;
            if (MixerPanel == null) return;
            MixerPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            EffectsPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            SoundsPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
            var radio = index == 0 ? TabMixer : index == 1 ? TabEffects : TabSounds;
            if (radio.IsChecked != true) radio.IsChecked = true;
            RefreshEffectsPanel();
        }

        private void RefreshEffectsPanel()
        {
            if (EffectsList == null) return;
            EffectsList.ItemsSource = _selectedTrack?.Effects;
            AddEffectButton.IsEnabled = _selectedTrack != null;
            if (_tabIndex == 1)
                PanelInfo.Text = _selectedTrack == null ? "Select a track to add effects." : $"Effects on {_selectedTrack.Name}";
            else if (_tabIndex == 0)
                PanelInfo.Text = "";
            else
                PanelInfo.Text = _selectedTrack == null ? "" : $"Adds to {_selectedTrack.Name}";
        }

        private void BuildEffectMenu()
        {
            AddEffectMenu.Items.Clear();
            foreach (var group in Services.Plugins.AllEffects.GroupBy(p => p.IsBuiltIn ? p.Category : "Plugins (USB)").OrderBy(g => g.Key))
            {
                var sub = new MenuFlyoutSubItem { Text = group.Key };
                foreach (var def in group.OrderBy(d => d.Name))
                {
                    var item = new MenuFlyoutItem { Text = def.Name, Tag = def };
                    item.Click += (s, e) => AddEffect((PluginDefinition)((FrameworkElement)s).Tag);
                    sub.Items.Add(item);
                }
                AddEffectMenu.Items.Add(sub);
            }
        }

        private void AddEffect(PluginDefinition def)
        {
            var track = _selectedTrack;
            if (track == null) return;
            if (track.Effects.Count >= 8) { SetStatus("A track can hold up to 8 effects."); return; }
            var fx = new EffectInstance(def);
            track.Effects.Add(fx);
            PushUndo("add effect", () => track.Effects.Remove(fx));
            Engine.Mixer.UpdateSnapshot(_project);
            SetStatus($"Added {def.Name} to {track.Name}.");
        }

        private EffectInstance TagFx(object sender) => (sender as FrameworkElement)?.Tag as EffectInstance;

        private void FxLeft_Click(object sender, RoutedEventArgs e) => MoveFx(TagFx(sender), -1);
        private void FxRight_Click(object sender, RoutedEventArgs e) => MoveFx(TagFx(sender), 1);

        private void MoveFx(EffectInstance fx, int dir)
        {
            var list = _selectedTrack?.Effects;
            if (fx == null || list == null) return;
            int i = list.IndexOf(fx), j = i + dir;
            if (i < 0 || j < 0 || j >= list.Count) return;
            list.Move(i, j);
            Engine.Mixer.UpdateSnapshot(_project);
        }

        private void FxRemove_Click(object sender, RoutedEventArgs e)
        {
            var fx = TagFx(sender);
            var track = _selectedTrack;
            if (fx == null || track == null) return;
            int idx = track.Effects.IndexOf(fx);
            track.Effects.Remove(fx);
            PushUndo("remove effect", () => track.Effects.Insert(Math.Min(idx, track.Effects.Count), fx));
            Engine.Mixer.UpdateSnapshot(_project);
        }

        // ------------------------------------------------------------------ sounds

        private async void Source_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            await LoadSoundsAsync();
        }

        private async Task LoadSoundsAsync()
        {
            var list = new List<SoundItem>();
            if (SrcUsb.IsChecked == true)
            {
                SoundsEmpty.Text = "Searching USB drives…";
                SoundsEmpty.Visibility = Visibility.Visible;
                SoundsList.ItemsSource = null;
                foreach (var entry in await Services.Usb.FindAudioAsync())
                    list.Add(new SoundItem
                    {
                        Name = System.IO.Path.GetFileNameWithoutExtension(entry.Name),
                        Category = "USB audio",
                        Source = entry.Location,
                        File = (StorageFile)entry.Item
                    });
                SoundsEmpty.Text = list.Count == 0
                    ? "No audio found. Put WAV/MP3/M4A files in the drive root or in Yursic\\Samples."
                    : "";
            }
            else
            {
                list.AddRange(await Services.Plugins.GetPluginSoundsAsync());
                list.AddRange(BuiltInSounds.All);
                SoundsEmpty.Text = "";
            }
            SoundsEmpty.Visibility = string.IsNullOrEmpty(SoundsEmpty.Text) ? Visibility.Collapsed : Visibility.Visible;
            SoundsList.ItemsSource = list;
        }

        private async void Sound_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is SoundItem sound)) return;
            if (Engine.IsRecording) return;
            var track = _selectedTrack ?? _project.Tracks.FirstOrDefault() ?? _project.AddTrack();
            ShowBusy($"Adding {sound.Name}…");
            try
            {
                int sr = Engine.SampleRate;
                var file = await sound.MaterializeAsync(_project.AudioFolder, sr);
                var buffer = await AudioImporter.LoadAsync(file, sr);

                bool empty = _project.Tracks.All(t => t.Clips.Count == 0);
                if (empty && sound.Bpm > 0)
                {
                    _project.Bpm = sound.Bpm;
                    BpmSlider.Value = sound.Bpm;
                    UpdateBpmText();
                }

                double start = Engine.PositionSeconds;
                var overlap = track.Clips.Where(c => c.StartSeconds < start + buffer.Seconds && c.EndSeconds > start)
                                         .OrderByDescending(c => c.EndSeconds).FirstOrDefault();
                if (overlap != null) start = overlap.EndSeconds;

                var clip = new Clip
                {
                    FileName = file.Name,
                    Name = sound.Name,
                    Buffer = buffer,
                    StartSeconds = start,
                    LengthSeconds = buffer.Seconds
                };
                track.Clips.Add(clip);
                PushUndo("add sound", () => track.Clips.Remove(clip));
                AfterEdit();
                SetStatus($"Added {sound.Name} to {track.Name} at {Format.Time(start)}.");
            }
            catch (Exception ex)
            {
                await Dialogs.InfoAsync("Couldn't add that sound", ex.Message);
            }
            finally
            {
                HideBusy();
            }
        }

        // ------------------------------------------------------------------ project / export

        private async void ProjectName_Click(object sender, RoutedEventArgs e)
        {
            var name = await Dialogs.PromptAsync("Rename project", "Name", _project.Name, "Rename");
            if (string.IsNullOrWhiteSpace(name)) return;
            _project.Name = name.Trim();
            ProjectNameButton.Content = _project.Name;
            await SaveAsync();
        }

        private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

        private async Task SaveAsync()
        {
            if (_project == null) return;
            try
            {
                await Services.Projects.SaveAsync(_project);
                SavedText.Text = "Saved " + DateTime.Now.ToString("t");
            }
            catch (Exception ex)
            {
                SavedText.Text = "Save failed";
                SetStatus(ex.Message);
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e) => _ = LeaveAsync();

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            double len = _project.LengthSeconds;
            if (len <= 0) { await Dialogs.InfoAsync("Nothing to export", "Add some clips first."); return; }
            if (Engine.IsRecording) await StopRecordingAsync();
            Engine.Pause();
            UpdateTransportVisuals();

            ShowBusy("Mixing down…");
            try
            {
                int sr = Engine.SampleRate;
                var data = await Task.Run(() => Engine.RenderOffline(len));
                var exports = await ApplicationData.Current.LocalFolder.CreateFolderAsync("Exports", CreationCollisionOption.OpenIfExists);
                string safe = string.Concat(_project.Name.Split(System.IO.Path.GetInvalidFileNameChars()));
                string name = $"{safe} {DateTime.Now:yyyy-MM-dd HHmm}.wav";
                await AudioImporter.SaveWavAsync(exports, name, data, sr);
                var file = await exports.GetFileAsync(name);
                HideBusy();

                var drives = await Services.Usb.GetDrivesAsync();
                if (drives.Count == 0)
                {
                    await Dialogs.InfoAsync("Exported", $"Saved \"{name}\" on this console. Plug in a USB drive and use USB Storage to copy it off.");
                    return;
                }
                if (!await Dialogs.ConfirmAsync("Exported", $"Mixdown \"{name}\" is ready. Copy it to your USB drive?", "Copy to USB"))
                    return;
                var drive = await Dialogs.PickDriveAsync();
                if (drive == null) return;
                ShowBusy("Copying to " + drive.Name + "…");
                await Services.Usb.CopyToDriveAsync(file, drive, "Exports");
                HideBusy();
                SetStatus($"Saved to {drive.Name}\\Yursic\\Exports\\{name}");
            }
            catch (Exception ex)
            {
                HideBusy();
                await Dialogs.InfoAsync("Export failed", ex.Message);
            }
        }
    }
}
