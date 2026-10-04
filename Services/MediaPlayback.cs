using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace ScheduleApp.Services;

/// <param name="Played">Số file đã phát hết.</param>
/// <param name="Problems">File không mở / không phát được (đã bỏ qua).</param>
/// <param name="StoppedByUser">Người dùng bấm Esc hoặc đóng cửa sổ trước khi phát hết.</param>
/// <param name="Skipped">Số file người dùng bấm → để bỏ qua.</param>
/// <param name="PlayedIndexes">Vị trí (trong danh sách) các file đã phát hết.</param>
internal sealed record PlaybackResult(int Played, IReadOnlyList<string> Problems, bool StoppedByUser, int Skipped = 0, IReadOnlyList<int>? PlayedIndexes = null);

/// <summary>
/// Trình phát video / nhạc của ScheduleApp (WPF MediaElement — dùng bộ giải mã có sẵn của Windows, không cần cài trình phát):
/// phát lần lượt từng file, hết file này tự sang file kế, phát xong cả danh sách mới trả về.
/// Phím tắt: Esc = dừng, → / N = sang file kế, Space = tạm dừng / phát tiếp.
/// </summary>
internal static class MediaPlayback
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".avi", ".wmv", ".mov", ".mpg", ".mpeg", ".webm", ".ts", ".3gp",
        ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac"
    };

    /// <summary>Chưa mở được file sau chừng này thì bỏ qua.</summary>
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Các file cần phát theo đúng thứ tự: mỗi dòng một file (bỏ dấu nháy, mở rộng %BIẾN_MÔI_TRƯỜNG%);
    /// dòng là thư mục → các file video / nhạc trong đó, sắp theo tên như Explorer (2 trước 10).
    /// </summary>
    public static (List<string> Files, List<string> Missing) Resolve(IEnumerable<string> lines)
    {
        var files = new List<string>();
        var missing = new List<string>();
        foreach (var line in lines)
        {
            var path = Environment.ExpandEnvironmentVariables(line.Trim().Trim('"', '\'').Trim());
            if (path.Length == 0) continue;
            if (File.Exists(path)) files.Add(Path.GetFullPath(path));
            else if (Directory.Exists(path))
                files.AddRange(Directory.GetFiles(path).Where(f => Extensions.Contains(Path.GetExtension(f)))
                    .Order(NameOrder));
            else missing.Add(path);
        }
        return (files, missing);
    }

    /// <summary>Sắp theo tên file như Explorer ("2.mp4" trước "10.mp4").</summary>
    internal static readonly IComparer<string> NameOrder =
        Comparer<string>.Create((a, b) => StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b)));

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    /// <summary>Kiểm thử: phát trong cửa sổ đặt ở vị trí này (ngoài màn hình), tắt tiếng, thay cho cài đặt của bước.</summary>
    internal static W.Rect? TestBounds { get; set; }

    /// <summary>Kiểm thử: màn hình được yêu cầu ở lần phát gần nhất (ghi lại trước khi <see cref="TestBounds"/> thay chỗ phát).</summary>
    internal static Native.Display? LastDisplay { get; set; }

    /// <summary>Phát lần lượt <paramref name="files"/> trong cửa sổ riêng (luồng riêng), trả về khi phát hết / bị dừng.</summary>
    /// <param name="volume">Âm lượng 0–100.</param>
    /// <param name="bounds">Vị trí cửa sổ (đơn vị WPF) khi không toàn màn hình; null = giữa màn hình chính.</param>
    /// <param name="durations">Thời lượng đã biết của từng file (hiện trên màn hình; phòng hờ khi file không báo đã phát hết).</param>
    /// <param name="display">Màn hình phát (xem <see cref="Native.Displays.Pick(int, string?)"/>): toàn màn hình thì phủ đúng màn hình đó,
    /// cửa sổ thường thì ở giữa màn hình đó (<see cref="Native.Displays.PlayerRect"/>); null = màn hình chính.</param>
    public static Task<PlaybackResult> PlayAsync(IReadOnlyList<string> files, bool fullscreen, int volume, CancellationToken ct, W.Rect? bounds = null,
        IReadOnlyList<TimeSpan?>? durations = null, Native.Display? display = null)
    {
        LastDisplay = display;
        if (TestBounds is { } test) (fullscreen, volume, bounds, display) = (false, 0, test, null);
        var done = new TaskCompletionSource<PlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var window = new PlayerWindow(files, durations, fullscreen, volume, bounds, display);
                window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                using (ct.Register(() => window.Dispatcher.BeginInvoke(window.Cancel)))
                {
                    window.Show();
                    Dispatcher.Run();
                }
                if (ct.IsCancellationRequested) done.TrySetCanceled(ct);
                else done.TrySetResult(window.Result);
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        }) { IsBackground = true, Name = "ScheduleApp — phát video" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private sealed class PlayerWindow : W.Window
    {
        /// <summary>File không báo đã phát hết: quá thời lượng đã biết chừng này thì sang file kế.</summary>
        private static readonly TimeSpan EndGrace = TimeSpan.FromSeconds(30);

        private readonly IReadOnlyList<string> _files;
        private readonly IReadOnlyList<TimeSpan?> _durations;
        /// <summary>Thời gian đã phát file hiện tại (không tính lúc tạm dừng).</summary>
        private readonly Stopwatch _playTime = new();
        private readonly WC.MediaElement _media = new()
        {
            LoadedBehavior = WC.MediaState.Manual, UnloadedBehavior = WC.MediaState.Manual, Stretch = WM.Stretch.Uniform
        };
        private readonly WC.TextBlock _caption = new()
        {
            Foreground = WM.Brushes.White, FontSize = 18, Margin = new W.Thickness(24), Padding = new W.Thickness(12, 6, 12, 6),
            Background = new WM.SolidColorBrush(WM.Color.FromArgb(150, 0, 0, 0)),
            VerticalAlignment = W.VerticalAlignment.Top, HorizontalAlignment = W.HorizontalAlignment.Left
        };
        private readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _captionTimer = new() { Interval = TimeSpan.FromSeconds(4) };
        private readonly List<string> _problems = [];
        private int _index = -1, _played, _skipped, _atEnd;
        private readonly List<int> _playedIndexes = [];
        private bool _opened, _paused, _finished, _stopped, _cancelled;
        private DateTime _openedAt;

        public PlaybackResult Result => new(_played, _problems, _stopped, _skipped, _playedIndexes);

        public PlayerWindow(IReadOnlyList<string> files, IReadOnlyList<TimeSpan?>? durations, bool fullscreen, int volume, W.Rect? bounds,
            Native.Display? display = null)
        {
            _files = files;
            _durations = durations is { } d && d.Count == files.Count ? d : files.Select(_ => (TimeSpan?)null).ToList();
            Title = "ScheduleApp — phát video";
            Background = WM.Brushes.Black;
            var grid = new WC.Grid();
            grid.Children.Add(_media);
            grid.Children.Add(_caption);
            Content = grid;
            _media.Volume = Math.Clamp(volume, 0, 100) / 100.0;

            // Phát ở màn hình khác màn hình người dùng đang làm việc (vd máy chiếu) → không giành bàn phím
            // (xem Displays.TakesFocus). Cửa sổ đặt sẵn vị trí (kiểm thử) cũng không lấy focus.
            bool activate = bounds == null && (display == null || Native.Displays.TakesFocus(display));
            if (fullscreen)
            {
                WindowStyle = W.WindowStyle.None;
                ResizeMode = W.ResizeMode.NoResize;
                Topmost = true;
                // Màn hình đã chọn: phủ đúng khung màn hình đó (đặt ở SourceInitialized); không chọn → phóng to ở màn hình chính.
                if (display == null) WindowState = W.WindowState.Maximized;
                Cursor = WI.Cursors.None;
            }
            else if (bounds is { } b)
            {
                WindowStartupLocation = W.WindowStartupLocation.Manual;
                (Left, Top, Width, Height) = (b.X, b.Y, b.Width, b.Height);
            }
            else if (display == null)
            {
                (Width, Height) = (1280, 760);
                WindowStartupLocation = W.WindowStartupLocation.CenterScreen;
            }
            if (!activate) ShowActivated = false;
            if (display != null && bounds == null)
            {
                var target = Native.Displays.PlayerRect(display, fullscreen);
                WindowStartupLocation = W.WindowStartupLocation.Manual;
                SourceInitialized += (_, _) => Place(target);
                // Đặt lại khi đã hiện: phòng khi WPF co giãn cửa sổ theo DPI của màn hình mới sau lần đặt đầu.
                Loaded += (_, _) => Place(target);
                if (!activate)
                    Log.Info("      Phát ở màn hình khác — trình phát không lấy bàn phím của màn hình bạn đang làm việc: " +
                             "bấm vào video để dùng Esc / Space / →; Ctrl+Shift+Q vẫn dừng mọi flow.");
            }

            _media.MediaOpened += (_, _) =>
            {
                _opened = true;
                if (_paused) _playTime.Reset();
                else _playTime.Restart();
                // Chưa biết thời lượng trước (file không có metadata) → hiện theo trình phát.
                if (_durations[_index] == null && _media.NaturalDuration.HasTimeSpan) ShowCaption(Label(_media.NaturalDuration.TimeSpan));
            };
            _media.MediaEnded += (_, _) => Finished();
            _media.MediaFailed += (_, e) => Skip($"không phát được \"{Current}\": {e.ErrorException?.Message}");
            _watchdog.Tick += (_, _) => Watch();
            _captionTimer.Tick += (_, _) =>
            {
                _captionTimer.Stop();
                if (_media.HasVideo) _caption.Visibility = W.Visibility.Collapsed;
            };
            KeyDown += OnKey;
            Loaded += (_, _) =>
            {
                if (fullscreen && activate)
                {
                    // Chạy theo lịch / từ xa: cửa sổ khác đang được chọn → giành quyền nhận phím (Esc, →, Space).
                    Native.WindowHelper.Focus(new W.Interop.WindowInteropHelper(this).Handle);
                    Activate();
                    Focus();
                }
                _watchdog.Start();
                Next();
            };
            Closed += (_, _) =>
            {
                _watchdog.Stop();
                _media.Close();
                if (!_finished && !_cancelled) _stopped = true;
            };
        }

        /// <summary>
        /// Đặt cửa sổ đúng khung <paramref name="target"/> bằng Win32 (pixel thật — đơn vị WPF đổi theo DPI từng màn hình nên không dùng
        /// Left/Top được). Sang màn hình khác DPI, Windows báo WM_DPICHANGED và WPF co giãn cửa sổ theo tỉ lệ DPI → đặt lại lần nữa
        /// (lúc này đã ở đúng màn hình, DPI không đổi nữa).
        /// </summary>
        private void Place(System.Drawing.Rectangle target)
        {
            var hwnd = new W.Interop.WindowInteropHelper(this).Handle;
            for (int i = 0; i < 3 && hwnd != IntPtr.Zero; i++)
            {
                if (Native.Win32.GetWindowRect(hwnd, out var r) && System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) == target) return;
                Native.Win32.SetWindowPos(hwnd, IntPtr.Zero, target.X, target.Y, target.Width, target.Height,
                    Native.Win32.SWP_NOZORDER | Native.Win32.SWP_NOACTIVATE);
            }
        }

        private string Current => _index >= 0 && _index < _files.Count ? Path.GetFileName(_files[_index]) : "";

        /// <summary>"2/5 · Clip_2.mp4 · 0:14" — dùng cho chú thích, tiêu đề cửa sổ và nhật ký.</summary>
        private string Label(TimeSpan? duration = null)
        {
            duration ??= _index >= 0 && _index < _durations.Count ? _durations[_index] : null;
            return $"{_index + 1}/{_files.Count} · {Current}" + (duration is { } d ? " · " + Models.ActionStep.FormatDuration(d) : "");
        }

        /// <summary>Flow bị dừng (hủy) → đóng cửa sổ.</summary>
        public void Cancel()
        {
            _cancelled = true;
            Close();
        }

        private void Finished()
        {
            _played++;
            _playedIndexes.Add(_index);
            Next();
        }

        private void Skip(string problem)
        {
            _problems.Add(problem);
            Next();
        }

        private void Next()
        {
            _index++;
            (_opened, _paused, _atEnd, _openedAt) = (false, false, 0, DateTime.UtcNow);
            _playTime.Reset();
            if (_index >= _files.Count)
            {
                _finished = true;
                Close();
                return;
            }
            var label = Label();
            Log.Info("      ▶ " + label);
            Title = "ScheduleApp — " + label;
            ShowCaption(label);
            // Cùng file với lần trước: trình phát chỉ tua về đầu, không báo MediaOpened → đóng hẳn để mở lại.
            if (_index > 0 && string.Equals(_files[_index], _files[_index - 1], StringComparison.OrdinalIgnoreCase)) _media.Source = null;
            _media.Source = new Uri(_files[_index]);
            _media.Play();
        }

        private void ShowCaption(string text)
        {
            _caption.Text = text;
            _caption.Visibility = W.Visibility.Visible;
            _captionTimer.Stop();
            _captionTimer.Start();
        }

        /// <summary>Phòng hờ: file không mở được, hoặc đã tới cuối mà không có sự kiện kết thúc.</summary>
        private void Watch()
        {
            if (_paused || _index >= _files.Count) return;
            if (!_opened)
            {
                if (DateTime.UtcNow - _openedAt > OpenTimeout) Skip($"không mở được \"{Current}\" sau {OpenTimeout.TotalSeconds:0} giây");
                return;
            }
            if (!_media.NaturalDuration.HasTimeSpan)
            {
                // Trình phát không biết độ dài (và có thể không báo đã phát hết) → dựa vào thời lượng đọc được từ file.
                if (_durations[_index] is { } known && _playTime.Elapsed > known + EndGrace)
                {
                    Log.Warn($"      \"{Current}\" không báo đã phát hết — quá thời lượng {Models.ActionStep.FormatDuration(known)}, sang file kế.");
                    Finished();
                }
                return;
            }
            if (_media.Position >= _media.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(300))
            {
                if (++_atEnd >= 5) Finished();
            }
            else _atEnd = 0;
        }

        private void OnKey(object sender, WI.KeyEventArgs e)
        {
            switch (e.Key)
            {
                case WI.Key.Escape:
                    Log.Warn($"      Đã dừng phát ở \"{Current}\" (Esc).");
                    Close();
                    break;
                case WI.Key.Right or WI.Key.N or WI.Key.PageDown:
                    Log.Info($"      Bỏ qua \"{Current}\" (phím →).");
                    _skipped++;
                    Next();
                    break;
                case WI.Key.Space:
                    _paused = !_paused;
                    if (_paused)
                    {
                        _media.Pause();
                        _playTime.Stop();
                    }
                    else
                    {
                        _media.Play();
                        if (_opened) _playTime.Start();
                    }
                    ShowCaption(_paused ? "⏸ Tạm dừng — Space để phát tiếp" : Label());
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }
    }
}
