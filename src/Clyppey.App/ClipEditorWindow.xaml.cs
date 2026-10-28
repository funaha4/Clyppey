using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clyppey.App.Localization;
using Clyppey.Core;
using Clyppey.Core.Editing;
using Clyppey.Core.Ffmpeg;
using Clyppey.Core.Storage;

namespace Clyppey.App;

/// <summary>
/// Cuts dead time out of a saved clip.
///
/// This window only reads the clip file and writes a new one, so it never touches the capture
/// pipeline and the "save without re-encoding" rule for the replay buffer still holds. The
/// actual cutting lives in <see cref="TrimJob"/>; this is the preview plus the two hands the
/// user needs (mark start, mark end).
/// </summary>
public partial class ClipEditorWindow : Window
{
    private readonly ClipEntry _clip;
    private readonly string _ffmpegPath;
    private readonly Action<string, double>? _onSaved;
    private readonly IReadOnlyList<TimeRange>? _initialCuts;
    private readonly TimeRange? _initialSelection;
    private readonly double? _initialPin;
    private readonly double? _initialViewSeconds;
    private readonly CutList _cuts = new();
    private readonly DispatcherTimer _timer;
    private readonly TaskCompletionSource<bool> _loadedSignal = new();

    private MediaInfo? _media;
    private IReadOnlyList<double> _keyframes = Array.Empty<double>();
    private Border? _playhead;
    private Border? _focusFill;
    private Border? _focusLeft;
    private Border? _focusRight;
    private Border? _handleLeft;
    private Border? _handleRight;
    private TimeRange? _selection;
    private bool _dragging;
    private bool _edgeDrag;
    private bool _edgeIsStart;
    private bool _activeEdgeIsStart;
    private bool _edgeArmed;
    private double _edgeFixed;
    private double _dragAnchor;
    private TimeRange? _selectionBeforeDrag;
    private bool _pointerOverTimeline;
    private bool _playerAuthoritative;
    private double _pointerSeconds;
    private double? _pinnedSeconds;
    private int _hoverEdge;
    private Brush _edgeBrush = Brushes.Aquamarine;
    private Brush _accentBrush = Brushes.Aquamarine;
    private Brush _warnBrush = Brushes.Gold;
    private const int PreviewSeekIntervalMs = 110;
    private const double MinSelectionSeconds = 0.1;
    private const double EdgeHitPixels = 6;
    private const double ClickSlopPixels = 4;

    // Timeline zoom + filmstrip geometry.
    private const double MinViewSeconds = 0.5;      // closest the timeline can zoom in
    private const double StripCellPixels = 18;      // screen width one thumbnail should cover
    private const int MaxStripBitmapWidth = 15_000; // stays inside WPF's 16k texture limit
    private const int MaxStripThumbs = 240;
    private const double StripKeyframeOnlyAbove = 0.75;  // seconds/thumb where decoding all frames is waste

    private DateTime _lastHoverSeekUtc = DateTime.MinValue;
    private double _lastHoverSeconds = -1;
    private double? _lastSkipTarget;
    private DateTime _lastSkipUtc = DateTime.MinValue;
    private bool _isPlaying;
    private bool _busy;
    private bool _mediaReady;
    private bool _previewWarned;
    private bool _confirmedClose;
    private DateTime _openedAtUtc = DateTime.UtcNow;

    // Only the visible slice of the clip is drawn. Zooming moves these two numbers; everything
    // on the timeline goes through XAt/TimeAt so the rest of the editor needed no changes.
    private double _viewStart;
    private double _viewEnd = -1;
    private double? _devViewSeconds;

    private WriteableBitmap? _stripBitmap;
    private double _stripStart;
    private double _stripEnd;
    private bool _stripComplete;
    private bool _stripBuilding;
    private bool _stripDirty;
    private DispatcherTimer? _stripDebounce;

    public ClipEditorWindow(
        ClipEntry clip,
        string ffmpegPath,
        Action<string, double>? onSaved = null,
        IReadOnlyList<TimeRange>? initialCuts = null,
        TimeRange? initialSelection = null,
        double? initialPin = null,
        int initialHoverEdge = 0,
        int initialArmedEdge = 0,
        double? initialViewSeconds = null)
    {
        InitializeComponent();

        _clip = clip;
        _ffmpegPath = ffmpegPath;
        _onSaved = onSaved;
        _initialCuts = initialCuts;
        _initialSelection = initialSelection;
        _initialPin = initialPin;
        _initialViewSeconds = initialViewSeconds;
        _hoverEdge = initialHoverEdge;

        if (initialArmedEdge != 0)
        {
            _edgeArmed = true;
            _activeEdgeIsStart = initialArmedEdge < 0;
        }

        ClipNameText.Text = clip.FileName;
        FileNameBox.Text = Path.GetFileNameWithoutExtension(clip.FileName) + "_cut.mp4";
        _accentBrush = (Brush)FindResource("AccentBrush");
        _warnBrush = (Brush)FindResource("WarnBrush");
        _edgeBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x5E, 0xEA, 0xD8));

        ModeBox.Items.Add(Loc.T("Trim.ModeExact"));
        ModeBox.Items.Add(Loc.T("Trim.ModeFast"));
        ModeBox.SelectedIndex = 0;
        UpdateSelectionUi();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        // The layout is fixed, so shrink it to the work area instead of opening a window whose
        // export button sits off-screen on a 1366x768 laptop.
        var workArea = SystemParameters.WorkArea;
        Width = Math.Clamp(workArea.Width - 60, 900, 1180);
        Height = Math.Clamp(workArea.Height - 60, 620, 820);

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _timer.Stop();
            try
            {
                Player.Stop();
                Player.Close();
            }
            catch (Exception)
            {
                // Nothing left to release.
            }
        };
    }

    private double CurrentSeconds =>
        _mediaReady && _media is not null
            ? Math.Clamp(Player.Position.TotalSeconds, 0, _media.Duration)
            : 0;

    /// <summary>Completes once the clip, its keyframes and the filmstrip are on screen.</summary>
    public Task LoadedTask => _loadedSignal.Task;

    /// <summary>
    /// Where the playhead should be drawn, and where a mark lands.
    ///
    /// While the pointer is on the timeline the answer is the pointer, not the player: seeking
    /// is throttled and Media Foundation finishes it asynchronously, so following the player
    /// made the playhead step along at about 9 Hz while the cursor moved smoothly.
    /// </summary>
    /// <summary>Which end the arrow keys adjust, or 0 when no end is armed.</summary>
    private int ArmedEdge => _edgeArmed ? (_activeEdgeIsStart ? -1 : 1) : 0;

    /// <summary>
    /// The end being worked on: dragged, hovered, or armed for the arrow keys. This is what the
    /// amber highlight follows, so it stays visible while nudging with the keyboard.
    /// </summary>
    private int ActiveEdge
    {
        get
        {
            if (_edgeDrag)
            {
                return _edgeIsStart ? -1 : 1;
            }

            return _hoverEdge != 0 ? _hoverEdge : ArmedEdge;
        }
    }

    private double DisplaySeconds =>
        _pinnedSeconds is { } pinned
            ? Math.Clamp(pinned, 0, _media?.Duration ?? pinned)
            : !_playerAuthoritative && _pointerOverTimeline && !_dragging && !_isPlaying
                ? _pointerSeconds
                : CurrentSeconds;

    // ---------------------------------------------------------------- loading

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Player.Source = new Uri(_clip.Path);

            // With LoadedBehavior=Manual, MediaElement only opens the file once it is asked to
            // do something with it. Nudging play/pause here makes MediaOpened fire on click,
            // instead of leaving the preview black until the user presses play.
            try
            {
                Player.Play();
                Player.Pause();
            }
            catch (Exception ex)
            {
                Log.Debug($"プレビューを開けません: {ex.Message}");
            }

            _media = await MediaProbe.ReadAsync(_ffmpegPath, _clip.Path).ConfigureAwait(true);

            if (_media is not { IsValid: true })
            {
                BusyText.Text = Loc.F("Trim.Failed", _media?.Error ?? "?");
                ExportButton.IsEnabled = false;
                return;
            }

            BusyText.Visibility = Visibility.Collapsed;
            _openedAtUtc = DateTime.UtcNow;

            // The whole clip fills the bar until the user zooms. Set before the first draw,
            // because every position on the timeline is derived from this window.
            _viewStart = 0;
            _viewEnd = _media.Duration;

            if (_initialViewSeconds is { } viewLength && viewLength > 0 && viewLength < _media.Duration)
            {
                // Development only: open zoomed in. Applied after the first strip so a snapshot
                // also exercises the "view changed, rebuild the strip" path.
                _devViewSeconds = Math.Max(MinViewSeconds, viewLength);
            }

            UpdateKeptText();
            RedrawTimeline();

            // The keyframe index only adds ticks to the bar, so it runs alongside the filmstrip
            // instead of in front of it. Measured on a 60 s clip: 1.8 s and ~3 s of ffmpeg that
            // used to be done one after the other.
            var keyframes = KeyframeIndex.ReadAsync(_ffmpegPath, _clip.Path);

            if (_initialCuts is { Count: > 0 })
            {
                foreach (var range in _initialCuts)
                {
                    _cuts.Add(range);
                }

                UpdateKeptText();
            }

            if (_initialSelection is { } initial)
            {
                _selection = initial;
                UpdateSelectionUi();
            }

            if (_initialPin is { } pin)
            {
                _pinnedSeconds = pin;
                ForcePreviewAt(pin);
            }

            RedrawTimeline();

            await Task.WhenAll(keyframes, EnsureFilmstripAsync(true)).ConfigureAwait(true);

            _keyframes = await keyframes.ConfigureAwait(true);
            RedrawTimeline();

            if (_devViewSeconds is { } devView && _media is { IsValid: true })
            {
                var anchor = _initialPin ?? (_media.Duration * 0.25);
                var length = Math.Min(devView, _media.Duration);
                SetView(anchor - (length * 0.3), length);
                await EnsureFilmstripAsync(true).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            BusyText.Text = Loc.F("Trim.Failed", ex.Message);
            Log.Warn($"エディタを開けません: {ex.Message}");
        }
        finally
        {
            _loadedSignal.TrySetResult(true);
        }
    }

    /// <summary>
    /// Thumbnails for the visible window, so the kills can be spotted without scrubbing.
    ///
    /// The strip used to be one ffmpeg run over the whole clip ("fps=N,tile=Nx1"), which had to
    /// decode every frame before anything appeared: 3.6 s for a 60 s clip, and nothing on screen
    /// while it ran. Here the window is cut into a few short runs, each painted into the bitmap
    /// as it arrives, so the left edge is visible in about a third of a second and the rest fills
    /// in. Zooming only re-reads the visible part of the file, which is why it stays responsive.
    /// </summary>
    private async Task EnsureFilmstripAsync(bool force = false)
    {
        if (_stripBuilding)
        {
            _stripDirty = true;
            return;
        }

        if (_media is not { IsValid: true } || TimelineCanvas.ActualWidth < 40)
        {
            return;
        }

        _stripBuilding = true;
        try
        {
            // This call owns the strip now; a queued debounce request would only abort it.
            _stripDebounce?.Stop();

            // A second pass covers a view change that happened while the strip was rendering.
            for (var pass = 0; pass < 3; pass++)
            {
                _stripDirty = false;

                if (!force && !StripNeedsRebuild())
                {
                    return;
                }

                force = false;
                await RenderFilmstripAsync().ConfigureAwait(true);

                if (!_stripDirty)
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"フィルムストリップを作れません: {ex.Message}");
        }
        finally
        {
            _stripBuilding = false;
        }
    }

    /// <summary>True when the current strip does not cover the view, or is too coarse for it.</summary>
    private bool StripNeedsRebuild()
    {
        if (_stripBitmap is null || !_stripComplete || _stripEnd - _stripStart <= 0.001
            || _media is not { IsValid: true } media)
        {
            return true;
        }

        var covered = _stripStart <= _viewStart + 0.001 && _stripEnd >= _viewEnd - 0.001;
        if (!covered)
        {
            return true;
        }

        // Both sides in thumbnails per second: the bitmap one has to be divided by the thumbnail
        // width, or a full-view strip would look dense enough for any zoom level.
        var cellsPerSecond = _stripBitmap.PixelWidth
            / (double)PlannedThumbSize(media).Width
            / (_stripEnd - _stripStart);

        return cellsPerSecond < (1.0 / PlannedSecondsPerThumb()) * 0.95;
    }

    private double PlannedSecondsPerThumb()
    {
        var span = Math.Max(0.2, _viewEnd - _viewStart);
        return span / Math.Max(2, PlannedThumbCount());
    }

    private int PlannedThumbCount()
    {
        if (_media is not { IsValid: true } media)
        {
            return 8;
        }

        var byPixels = (int)Math.Round(TimelineCanvas.ActualWidth / StripCellPixels);
        var byTexture = Math.Max(8, (int)(MaxStripBitmapWidth / (double)PlannedThumbSize(media).Width));

        return Math.Clamp(byPixels, 8, Math.Min(MaxStripThumbs, byTexture));
    }

    /// <summary>
    /// One thumbnail in pixels: as tall as the bar so the strip is never upscaled (it is drawn
    /// squashed horizontally, which is what gives the strip its smear), and frame-shaped so the
    /// picture is not distorted on top of that.
    /// </summary>
    private (int Width, int Height) PlannedThumbSize(MediaInfo media)
    {
        var height = Math.Clamp((int)Math.Round(TimelineCanvas.ActualHeight) - 2, 16, 200);
        var aspect = media.Width / (double)Math.Max(1, media.Height);
        var width = Math.Clamp((int)Math.Round(height * aspect), 8, 640);
        return (width, height);
    }

    private async Task RenderFilmstripAsync()
    {
        if (_media is not { IsValid: true } media)
        {
            return;
        }

        var width = TimelineCanvas.ActualWidth;
        if (width < 40)
        {
            return;
        }

        var start = _viewStart;
        var end = _viewEnd;
        var span = Math.Max(0.2, end - start);
        var thumbs = PlannedThumbCount();
        var secondsPerThumb = span / thumbs;
        var thumb = PlannedThumbSize(media);

        var bitmap = new WriteableBitmap(
            thumbs * thumb.Width, thumb.Height, 96, 96, PixelFormats.Bgra32, null);

        // Swap the strip in straight away: the unfilled part is transparent, so the bar keeps
        // its normal surface and the thumbnails fill it in from the left. "Complete" only means
        // every chunk landed; until then the next request must rebuild rather than trust it.
        _stripBitmap = bitmap;
        _stripStart = start;
        _stripEnd = end;
        _stripComplete = false;
        FilmstripImage.Source = bitmap;
        UpdateStripGeometry();

        // Long windows sample keyframes only (the capture writes one per second), which turns a
        // full-frame decode into a handful of frames. Short windows are cheap anyway and need
        // real frames to stay sharp, so they decode everything.
        var keyframeOnly = secondsPerThumb >= StripKeyframeOnlyAbove;

        var chunks = Math.Clamp((int)Math.Round(span / 7.0), 1, 8);
        var thumbsPerChunk = (int)Math.Ceiling(thumbs / (double)chunks);

        Directory.CreateDirectory(AppPaths.TempDir);

        for (var first = 0; first < thumbs; first += thumbsPerChunk)
        {
            var count = Math.Min(thumbsPerChunk, thumbs - first);
            var chunkStart = start + (first * secondsPerThumb);
            var chunkSeconds = count * secondsPerThumb;
            var file = Path.Combine(AppPaths.TempDir, $"strip_{Guid.NewGuid():N}.png");

            var rate = (1.0 / secondsPerThumb).ToString("0.######", CultureInfo.InvariantCulture);
            var filters = $"fps={rate},scale={thumb.Width}:{thumb.Height},tile={count}x1";

            var args = new List<string>
            {
                "-y", "-v", "error", "-nostdin",
                "-ss", chunkStart.ToString("0.###", CultureInfo.InvariantCulture)
            };

            if (keyframeOnly)
            {
                args.Add("-skip_frame");
                args.Add("nokey");
            }

            args.Add("-i");
            args.Add(_clip.Path);
            args.Add("-t");
            args.Add((chunkSeconds + 0.5).ToString("0.###", CultureInfo.InvariantCulture));
            args.Add("-an");
            args.Add("-vf");
            args.Add(filters);
            args.Add("-frames:v");
            args.Add("1");
            args.Add(file);

            try
            {
                var (success, error) = await ProcessRunner.RunAsync(_ffmpegPath, args, 20_000).ConfigureAwait(true);
                if (_stripDirty)
                {
                    // The user moved the view; this chunk is already obsolete.
                    return;
                }

                if (!success || !File.Exists(file))
                {
                    Log.Debug($"フィルムストリップの一部を作れません: {error}");
                    continue;
                }

                PaintStripChunk(bitmap, file, first * thumb.Width, count, thumb.Width, thumb.Height);
            }
            finally
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception)
                {
                    // A leftover temp image is harmless.
                }
            }
        }

        _stripComplete = true;
    }

    private static void PaintStripChunk(
        WriteableBitmap bitmap, string file, int left, int expected, int thumbWidth, int thumbHeight)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(file);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();

        // A short chunk (end of the clip) yields fewer tiles than planned; copy what exists.
        var copyWidth = Math.Min(image.PixelWidth, expected * thumbWidth);
        var copyHeight = Math.Min(image.PixelHeight, thumbHeight);
        if (copyWidth <= 0 || copyHeight <= 0 || left >= bitmap.PixelWidth)
        {
            return;
        }

        copyWidth = Math.Min(copyWidth, bitmap.PixelWidth - left);

        BitmapSource source = image;
        if (image.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            source = converted;
        }

        // WritePixels has no BitmapSource overload: copy the tiles into a byte[] first.
        var stride = copyWidth * 4;
        var buffer = new byte[stride * copyHeight];
        source.CopyPixels(new Int32Rect(0, 0, copyWidth, copyHeight), buffer, stride, 0);
        bitmap.WritePixels(new Int32Rect(left, 0, copyWidth, copyHeight), buffer, stride, 0);
    }

    /// <summary>Places the strip over the window it was rendered for, which moves as the view does.</summary>
    private void UpdateStripGeometry()
    {
        if (_stripBitmap is null || _media is null || TimelineCanvas.ActualWidth < 20)
        {
            FilmstripImage.Source = null;
            return;
        }

        var left = XAt(_stripStart);
        var right = XAt(_stripEnd);
        var width = Math.Max(1.0, right - left);

        FilmstripImage.Width = width;
        FilmstripImage.Margin = new Thickness(left, 1, 0, 1);
    }

    // ---------------------------------------------------------------- view window

    /// <summary>Length of the clip, or 0 before the probe has finished.</summary>
    private double FullSeconds => _media?.Duration ?? 0;

    /// <summary>Seconds currently mapped onto the full width of the timeline.</summary>
    private double ViewLength => Math.Max(0.001, _viewEnd - _viewStart);

    private bool IsZoomed => _media is not null && ViewLength < FullSeconds - 0.01;

    /// <summary>Timeline x for a clip time. The only place the zoom is applied to drawing.</summary>
    private double XAt(double seconds) =>
        (seconds - _viewStart) / ViewLength * TimelineCanvas.ActualWidth;

    /// <summary>Clip time under a timeline x. The only place the zoom is applied to input.</summary>
    private double TimeAt(double x)
    {
        var width = TimelineCanvas.ActualWidth;
        if (_media is null || width <= 0)
        {
            return _viewStart;
        }

        return _viewStart + Math.Clamp(x / width, 0, 1) * ViewLength;
    }

    private double EdgeToleranceSeconds()
    {
        var width = TimelineCanvas.ActualWidth;
        return width < 20 ? 0 : EdgeHitPixels / width * ViewLength;
    }

    /// <summary>Shows [start, start + length] and refreshes everything that depends on it.</summary>
    private void SetView(double start, double length)
    {
        if (_media is null)
        {
            return;
        }

        var full = FullSeconds;
        length = Math.Clamp(length, Math.Min(MinViewSeconds, full), full);
        start = Math.Clamp(start, 0, Math.Max(0, full - length));

        _viewStart = start;
        _viewEnd = Math.Min(full, start + length);

        RedrawTimeline();
        UpdateZoomUi();
        RequestFilmstrip();
    }

    private void FitView()
    {
        if (_media is null)
        {
            return;
        }

        SetView(0, FullSeconds);
    }

    /// <summary>Zooms about a clip time, so whatever is under the cursor stays under it.</summary>
    private void ZoomAround(double anchorSeconds, double factor)
    {
        if (_media is null)
        {
            return;
        }

        var width = TimelineCanvas.ActualWidth;
        var fraction = width > 1 ? Math.Clamp((anchorSeconds - _viewStart) / ViewLength, 0, 1) : 0.5;
        var length = Math.Clamp(ViewLength * factor, Math.Min(MinViewSeconds, FullSeconds), FullSeconds);
        SetView(anchorSeconds - (fraction * length), length);
    }

    private void UpdateZoomUi()
    {
        var zoom = FullSeconds / ViewLength;
        ZoomText.Text = Math.Abs(zoom - 1) < 0.005
            ? string.Empty
            : Loc.F("Trim.Zoom", zoom.ToString(zoom < 10 ? "0.0" : "0", CultureInfo.InvariantCulture));
        FitButton.Visibility = IsZoomed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Timeline_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_media is null || _busy)
        {
            return;
        }

        var steps = e.Delta / 120.0;
        if (steps == 0)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // Wheel up zooms in. 1.25 per notch keeps a 60 s clip reachable in about a dozen
            // turns instead of overshooting on a free-spinning wheel.
            ZoomAround(TimeAt(e.GetPosition(TimelineCanvas).X), Math.Pow(1 / 1.25, steps));
        }
        else if (IsZoomed)
        {
            PanBy(-steps * ViewLength * 0.12);
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private void PanBy(double deltaSeconds)
    {
        if (!IsZoomed)
        {
            return;
        }

        SetView(_viewStart + deltaSeconds, ViewLength);
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        SetPlaying(false);
        FitView();
    }

    /// <summary>Pushes the view along while playing, so a zoomed-in playhead cannot leave it.</summary>
    private void FollowPlayheadDuringPlayback()
    {
        if (!_isPlaying || _media is null)
        {
            return;
        }

        var length = ViewLength;
        if (length >= FullSeconds - 0.01)
        {
            return;
        }

        var seconds = CurrentSeconds;
        if (seconds >= _viewStart && seconds <= _viewEnd - (length * 0.02))
        {
            return;
        }

        SetView(seconds - (length * 0.1), length);
    }

    private void RequestFilmstrip()
    {
        _stripDirty = true;

        if (_stripDebounce is null)
        {
            _stripDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
            _stripDebounce.Tick += (_, _) =>
            {
                _stripDebounce!.Stop();
                _ = EnsureFilmstripAsync();
            };
        }

        _stripDebounce.Stop();
        _stripDebounce.Start();
    }

    // ---------------------------------------------------------------- timeline

    private void RedrawTimeline()
    {
        var width = TimelineCanvas.ActualWidth;
        var height = TimelineCanvas.ActualHeight;
        if (_media is null || width < 20 || height < 10)
        {
            return;
        }

        TimelineCanvas.Children.Clear();
        _playhead = null;
        _focusFill = null;
        _focusLeft = null;
        _focusRight = null;
        _handleLeft = null;
        _handleRight = null;

        // Keyframe ticks: the only places a copy-mode cut can start.
        foreach (var keyframe in _keyframes)
        {
            var tick = new Border
            {
                Width = 1,
                Height = height * 0.18,
                Background = new SolidColorBrush(Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF))
            };
            Canvas.SetLeft(tick, XAt(keyframe));
            Canvas.SetTop(tick, height * 0.82);
            TimelineCanvas.Children.Add(tick);
        }

        foreach (var range in _cuts.Removed)
        {
            var boxWidth = Math.Max(3, (range.End - range.Start) / ViewLength * width);
            var box = new Border
            {
                Width = boxWidth,
                Height = height,
                Background = new SolidColorBrush(Color.FromArgb(0xA8, 0xFF, 0x6B, 0x4A)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x9A, 0x80)),
                BorderThickness = new Thickness(1, 0, 1, 0)
            };

            if (boxWidth > 56)
            {
                // Colour alone would not tell the user what the band means.
                box.Child = new TextBlock
                {
                    Text = Loc.T("Trim.Removed"),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x0A, 0x06))
                };
            }

            Canvas.SetLeft(box, XAt(range.Start));
            TimelineCanvas.Children.Add(box);
        }

        // The band the user is pointing at: a light fill plus two crisp edge strips.
        //
        // The edges are separate 2 px Borders rather than one Border with BorderThickness:
        // a border 4 px thick on a band a few pixels wide paints outside the band, which left
        // ghost lines behind while a new selection was being dragged.
        var focusFill = new Border
        {
            Height = height,
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x2D, 0xD4, 0xBF)),
            Visibility = Visibility.Collapsed
        };
        var focusLeft = new Border
        {
            Width = 2,
            Height = height,
            Background = _edgeBrush,
            Visibility = Visibility.Collapsed
        };
        var focusRight = new Border
        {
            Width = 2,
            Height = height,
            Background = _edgeBrush,
            Visibility = Visibility.Collapsed
        };

        TimelineCanvas.Children.Add(focusFill);
        TimelineCanvas.Children.Add(focusLeft);
        TimelineCanvas.Children.Add(focusRight);

        // Grabbable ends: the band edges can be dragged to re-adjust an existing selection.
        var handleLeft = CreateHandle();
        var handleRight = CreateHandle();
        TimelineCanvas.Children.Add(handleLeft);
        TimelineCanvas.Children.Add(handleRight);
        _handleLeft = handleLeft;
        _handleRight = handleRight;

        _focusFill = focusFill;
        _focusLeft = focusLeft;
        _focusRight = focusRight;
        UpdateFocusGeometry();

        var playhead = new Border
        {
            Width = 2,
            Height = height,
            Background = (Brush)FindResource("AccentBrush")
        };
        Canvas.SetLeft(playhead, XAt(CurrentSeconds));
        TimelineCanvas.Children.Add(playhead);
        _playhead = playhead;

        UpdateStripGeometry();
        UpdateZoomUi();
    }

    /// <summary>Whatever the user is currently pointing at (a dragged or adjusted range).</summary>
    private TimeRange? FocusRange =>
        _selection is { } selection && selection.Duration > 0.01 ? selection : null;

    private void UpdateFocusGeometry()
    {
        if (_media is null || _focusFill is null || _focusLeft is null || _focusRight is null)
        {
            return;
        }

        var width = TimelineCanvas.ActualWidth;
        if (width < 20)
        {
            return;
        }

        var range = FocusRange;
        if (range is not { } band)
        {
            _focusFill.Visibility = Visibility.Collapsed;
            _focusLeft.Visibility = Visibility.Collapsed;
            _focusRight.Visibility = Visibility.Collapsed;
            return;
        }

        var from = XAt(band.Start);
        var to = XAt(band.End);
        var pixels = Math.Max(1, to - from);

        _focusFill.Visibility = Visibility.Visible;
        _focusFill.Width = pixels;
        Canvas.SetLeft(_focusFill, from);

        // The end being worked on turns amber and thicker: dragged, hovered, or armed for the
        // arrow keys. That way the state stays visible while nudging rather than only on click.
        var hot = ActiveEdge;

        var leftHot = hot == -1;
        var leftHover = !leftHot && _hoverEdge == -1;
        _focusLeft.Width = leftHot ? 4 : leftHover ? 3 : 2;
        _focusLeft.Background = leftHot ? _warnBrush : _edgeBrush;
        _focusLeft.Visibility = Visibility.Visible;
        Canvas.SetLeft(_focusLeft, leftHot ? from - 1 : from);

        var rightHot = hot == 1;
        var rightHover = !rightHot && _hoverEdge == 1;
        _focusRight.Width = rightHot ? 4 : rightHover ? 3 : 2;
        _focusRight.Background = rightHot ? _warnBrush : _edgeBrush;

        if (rightHot || rightHover)
        {
            _focusRight.Visibility = Visibility.Visible;
            Canvas.SetLeft(_focusRight, rightHot ? to - 3 : to - 2);
        }
        else if (pixels < 6)
        {
            // Two edges on top of each other just reads as a thicker line, so one is enough.
            _focusRight.Visibility = Visibility.Collapsed;
        }
        else
        {
            _focusRight.Visibility = Visibility.Visible;
            Canvas.SetLeft(_focusRight, to - 2);
        }

        // Handles only exist for a real selection; the I/O preview range has nothing to grab.
        var grabbable = _selection is not null;
        var canvasHeight = TimelineCanvas.ActualHeight;

        ApplyHandle(_handleLeft, grabbable, leftHot, leftHover, from, canvasHeight);
        ApplyHandle(_handleRight, grabbable, rightHot, rightHover, to, canvasHeight);
    }

    private void ApplyHandle(
        Border? handle, bool visible, bool hot, bool hover, double x, double canvasHeight)
    {
        if (handle is null)
        {
            return;
        }

        if (!visible)
        {
            handle.Visibility = Visibility.Collapsed;
            return;
        }

        var width = hot ? 12.0 : hover ? 10.0 : 8.0;
        var height = hot ? 26.0 : hover ? 20.0 : 16.0;

        handle.Visibility = Visibility.Visible;
        handle.Width = width;
        handle.Height = height;
        handle.CornerRadius = new CornerRadius(hot ? 5 : 3);
        handle.Background = hot ? _warnBrush : _edgeBrush;
        Canvas.SetLeft(handle, x - (width / 2));
        Canvas.SetTop(handle, Math.Max(0, (canvasHeight - height) / 2));
    }

    private static Border CreateHandle() => new()
    {
        Width = 8,
        Height = 16,
        CornerRadius = new CornerRadius(3),
        Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x5E, 0xEA, 0xD8)),
        Visibility = Visibility.Collapsed
    };

    /// <summary>-1 when the pointer is on the selection start, 1 on the end, 0 anywhere else.</summary>
    private int HitTestEdge(double seconds)
    {
        if (_media is null || _selection is not { } selection)
        {
            return 0;
        }

        var width = TimelineCanvas.ActualWidth;
        if (width < 20)
        {
            return 0;
        }

        var tolerance = EdgeToleranceSeconds();
        if (Math.Abs(seconds - selection.Start) <= tolerance)
        {
            return -1;
        }

        return Math.Abs(seconds - selection.End) <= tolerance ? 1 : 0;
    }

    private void UpdatePlayhead(double seconds)
    {
        if (_playhead is null || _media is null)
        {
            return;
        }

        // While dragging, the moving edge of the selection already marks the cursor. Leaving
        // the playhead at the previous position made the old edge look like a leftover.
        // Hidden while dragging (the moving band edge is the cursor indicator) and whenever the
        // pointer is away from the timeline: a line parked at the last position reads as a
        // leftover. Playback and an explicit pin still keep it on screen.
        var visible = !_dragging && (_pointerOverTimeline || _isPlaying || _pinnedSeconds is not null);
        if (!visible)
        {
            _playhead.Visibility = Visibility.Collapsed;
            return;
        }

        _playhead.Visibility = Visibility.Visible;

        // A pinned playhead is amber and says so in the readout; colour alone would not carry it.
        _playhead.Background = _pinnedSeconds is not null ? _warnBrush : _accentBrush;

        var width = TimelineCanvas.ActualWidth;
        if (width < 20)
        {
            return;
        }

        // Zoomed in, the playhead can sit outside the window; there is nothing to draw then.
        if (seconds < _viewStart - 0.0005 || seconds > _viewEnd + 0.0005)
        {
            _playhead.Visibility = Visibility.Collapsed;
            return;
        }

        Canvas.SetLeft(_playhead, Math.Clamp(XAt(seconds), 0, width - 2));
    }

    private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawTimeline();
        RequestFilmstrip();
    }

    private double SecondsAt(Point point) => TimeAt(point.X);

    private void Timeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_media is null || _busy)
        {
            return;
        }

        SetPlaying(false);
        _pinnedSeconds = null;

        var pointer = SecondsAt(e.GetPosition(TimelineCanvas));
        var edge = HitTestEdge(pointer);

        if (edge != 0 && _selection is { } existing)
        {
            // Grabbing an end of the current selection adjusts it instead of starting a new one.
            _dragging = true;
            _edgeDrag = true;
            _edgeIsStart = edge < 0;
            _activeEdgeIsStart = _edgeIsStart;
            _edgeArmed = true;
            _hoverEdge = edge;
            _edgeFixed = _edgeIsStart ? existing.End : existing.Start;
            TimelineCanvas.CaptureMouse();

            _lastHoverSeekUtc = DateTime.MinValue;
            ScrubPreviewAt(pointer);
            UpdateFocusGeometry();
            UpdateSelectionUi();
            UpdatePlayhead(pointer);
            UpdateTimeText(pointer);
            return;
        }

        _edgeDrag = false;
        _dragAnchor = pointer;
        _dragging = true;
        _selectionBeforeDrag = _selection;
        _selection = null;
        TimelineCanvas.CaptureMouse();

        // Do not let a hover seek that just happened swallow the first frame of the drag.
        _lastHoverSeekUtc = DateTime.MinValue;
        ScrubPreviewAt(_dragAnchor);

        UpdateFocusGeometry();
        UpdateSelectionUi();
        UpdatePlayhead(_dragAnchor);
        UpdateTimeText(_dragAnchor);
    }

    private void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        if (_media is null)
        {
            return;
        }

        // The mouse is pointing again, so the cursor wins over the last seek.
        _playerAuthoritative = false;

        // A pinned playhead ignores the mouse entirely until it is released.
        if (_pinnedSeconds is not null)
        {
            return;
        }

        var pointer = SecondsAt(e.GetPosition(TimelineCanvas));
        _pointerOverTimeline = true;
        _pointerSeconds = pointer;

        if (!_dragging)
        {
            // The line follows the cursor at mouse rate; the picture catches up on a throttle.
            // While playback runs the playhead belongs to the player, not to the mouse.
            if (!_isPlaying)
            {
                var edge = HitTestEdge(pointer);
                if (edge != _hoverEdge)
                {
                    // Light up the end under the pointer so it is obvious what can be grabbed.
                    _hoverEdge = edge;
                    UpdateFocusGeometry();
                    UpdateSelectionUi();
                }

                TimelineCanvas.Cursor = edge != 0 ? Cursors.SizeWE : Cursors.Hand;
                UpdatePlayhead(pointer);
                UpdateTimeText(pointer);
                ScrubPreviewAt(pointer);
            }

            return;
        }

        // Only the band is redrawn on every mouse event; the preview follows on the same
        // throttle as hovering, because a Media Foundation seek per event would fall behind.
        if (_edgeDrag)
        {
            var fixedEdge = _edgeFixed;
            _selection = _edgeIsStart
                ? new TimeRange(
                    Math.Clamp(pointer, 0, Math.Max(0, fixedEdge - MinSelectionSeconds)),
                    fixedEdge)
                : new TimeRange(
                    fixedEdge,
                    Math.Clamp(pointer, Math.Min(_media.Duration, fixedEdge + MinSelectionSeconds), _media.Duration));
        }
        else
        {
            // Below the slop this is a click, not a drag: the existing selection must survive it.
            var candidate = TimeRange.Normalized(_dragAnchor, pointer);
            var slop = ClickSlopPixels / Math.Max(1.0, TimelineCanvas.ActualWidth) * ViewLength;
            _selection = candidate.Duration >= slop ? candidate : null;
        }

        ScrubPreviewAt(pointer);
        UpdateFocusGeometry();
        UpdateSelectionUi();
        UpdatePlayhead(pointer);
        UpdateTimeText(pointer);
    }

    /// <summary>
    /// Moves the preview to the hovered frame. Throttled: Media Foundation seeks take tens of
    /// milliseconds, so following every mouse event would fall behind and look stuttery.
    /// </summary>
    private void ScrubPreviewAt(double seconds)
    {
        if (_media is null || _isPlaying || _busy)
        {
            return;
        }

        if (Math.Abs(seconds - _lastHoverSeconds) < 0.02)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastHoverSeekUtc).TotalMilliseconds < PreviewSeekIntervalMs)
        {
            return;
        }

        _lastHoverSeekUtc = now;
        _lastHoverSeconds = seconds;

        try
        {
            Player.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _media.Duration));
        }
        catch (Exception)
        {
            // Position is only settable once the media is open.
        }
    }

    private void Timeline_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var wasEdgeDrag = _edgeDrag;
        _edgeDrag = false;
        _dragging = false;
        _hoverEdge = 0;
        if (TimelineCanvas.IsMouseCaptured)
        {
            TimelineCanvas.ReleaseMouseCapture();
        }

        var seconds = SecondsAt(e.GetPosition(TimelineCanvas));

        if (wasEdgeDrag && _selection is { } adjusted)
        {
            // Re-adjusted an existing end: keep the selection so it can be nudged again.
            _edgeArmed = true;
            SeekTo(_edgeIsStart ? adjusted.Start : adjusted.End);
            RedrawTimeline();
            UpdateSelectionUi();
            return;
        }

        if (_selection is { } selection && selection.Duration >= MinSelectionSeconds)
        {
            // Whichever end the drag finished on becomes the one the arrow keys nudge.
            _activeEdgeIsStart = _dragAnchor > seconds;
            _edgeArmed = true;

            // Park the playhead on the out point so the cut can be checked right away.
            SeekTo(selection.End);
        }
        else
        {
            // A click only moves the playhead; it never throws away the current selection.
            _selection = _selectionBeforeDrag;
            _edgeArmed = false;
            SeekTo(seconds);
        }

        RedrawTimeline();
        UpdateSelectionUi();
    }

    private void Timeline_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        // Alt+Tab or another window stealing the capture must not leave the tool stuck
        // half-way through a drag.
        _dragging = false;
        _edgeDrag = false;
        _hoverEdge = 0;
        RedrawTimeline();
        UpdateSelectionUi();
    }

    private void Timeline_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            return;
        }

        _pointerOverTimeline = false;
        _playerAuthoritative = false;
        _hoverEdge = 0;
        UpdateFocusGeometry();
        UpdateSelectionUi();
        OnTick();
    }

    /// <summary>
    /// Right-click pins the playhead where it is: the pointer stops dragging both the line and
    /// the preview along, so a spot can be held while looking around it. Right-click again
    /// (or click, seek, or play) releases it.
    /// </summary>
    private void Timeline_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_media is null || _busy)
        {
            return;
        }

        e.Handled = true;

        if (_pinnedSeconds is not null)
        {
            Unpin();
            return;
        }

        var seconds = SecondsAt(e.GetPosition(TimelineCanvas));
        _pinnedSeconds = seconds;
        _pointerOverTimeline = false;
        ForcePreviewAt(seconds);
        UpdatePlayhead(seconds);
        UpdateTimeText(seconds);
        UpdateFocusGeometry();
    }

    private void Unpin()
    {
        if (_pinnedSeconds is null)
        {
            return;
        }

        _pinnedSeconds = null;

        // Let the next hover seek through immediately instead of waiting out the throttle.
        _lastHoverSeconds = -1;
        _lastHoverSeekUtc = DateTime.MinValue;
        OnTick();
    }

    /// <summary>Puts the preview on a specific frame, bypassing the hover throttle.</summary>
    private void ForcePreviewAt(double seconds)
    {
        if (_media is null)
        {
            return;
        }

        _lastHoverSeconds = seconds;
        _lastHoverSeekUtc = DateTime.UtcNow;

        try
        {
            Player.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _media.Duration));
        }
        catch (Exception)
        {
            // Position is only settable once the media is open.
        }
    }

    private void UpdateTimeText(double seconds)
    {
        if (_media is null)
        {
            return;
        }

        TimeText.Text = _pinnedSeconds is not null
            ? Loc.F("Trim.PinnedTime", MMss(seconds), MMss(_media.Duration))
            : $"{MMss(seconds)} / {MMss(_media.Duration)}";
    }

    /// <summary>
    /// Moves the player past a range marked for removal, so playback previews the edit.
    /// Returns true when the position was changed.
    /// </summary>
    private bool SkipRemovedDuringPlayback()
    {
        if (_media is null)
        {
            return false;
        }

        var position = Player.Position.TotalSeconds;
        var target = position;

        // One pass is enough for merged ranges, but a jump can land inside the next one.
        for (var guard = 0; guard < 8; guard++)
        {
            var moved = false;
            foreach (var range in _cuts.Removed)
            {
                if (target >= range.Start - 0.001 && target < range.End - 0.02)
                {
                    // Seek to a keyframe, because Media Foundation lands on the keyframe at or
                    // BEFORE the requested time. Asking for "end + 1 ms" landed back inside the
                    // removed range, got skipped again and looped.
                    var resume = range.End + 0.02;
                    var before = KeyframeIndex.Before(_keyframes, resume);
                    if (before >= range.End - 0.05 && before > range.Start)
                    {
                        // That keyframe is already past the cut, so nothing extra is lost.
                        target = before;
                    }
                    else
                    {
                        var after = KeyframeIndex.AtOrAfter(_keyframes, resume);
                        target = double.IsPositiveInfinity(after) ? resume : Math.Max(resume, after);
                    }
                    moved = true;
                    break;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        if (Math.Abs(target - position) < 0.001)
        {
            return false;
        }

        // Media Foundation applies a seek asynchronously, so the same jump would otherwise be
        // issued again on the next tick while the position still reads the old value.
        var now = DateTime.UtcNow;
        if (_lastSkipTarget is { } last &&
            Math.Abs(last - target) < 0.001 &&
            (now - _lastSkipUtc) < TimeSpan.FromMilliseconds(400))
        {
            return false;
        }

        _lastSkipTarget = target;
        _lastSkipUtc = now;

        try
        {
            Player.Position = TimeSpan.FromSeconds(Math.Clamp(target, 0, _media.Duration));
        }
        catch (Exception)
        {
            return false;
        }

        return true;
    }

    private void OnTick()
    {
        if (_media is null)
        {
            return;
        }

        if (_dragging)
        {
            // MouseMove owns the readouts during a drag; the player position lags behind it.
            return;
        }

        var seconds = DisplaySeconds;
        UpdatePlayhead(seconds);
        UpdateFocusGeometry();
        UpdateTimeText(seconds);

        // Playing shows the edited result: ranges marked for removal are jumped over.
        if (_isPlaying && SkipRemovedDuringPlayback())
        {
            seconds = CurrentSeconds;
            UpdatePlayhead(seconds);
            UpdateTimeText(seconds);
        }

        if (_isPlaying && Player.Position.TotalSeconds >= _media.Duration - 0.05)
        {
            SetPlaying(false);
        }

        // Zoomed in, the view follows playback instead of letting the playhead run off the bar.
        FollowPlayheadDuringPlayback();

        // Some clips open for probing but never hand a frame to Media Foundation (unusual
        // profiles). Say so instead of leaving the user with a black rectangle.
        if (!_mediaReady && !_previewWarned && (DateTime.UtcNow - _openedAtUtc).TotalSeconds > 3)
        {
            _previewWarned = true;
            BusyText.Text = Loc.T("Trim.NoPreviewShort");
            BusyText.Visibility = Visibility.Visible;
        }
    }

    // ---------------------------------------------------------------- transport

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (_media is null || _busy)
        {
            return;
        }

        _pinnedSeconds = null;
        SeekTo(0);

        try
        {
            Player.Play();
            SetPlaying(true);
        }
        catch (Exception ex)
        {
            Log.Debug($"再生できません: {ex.Message}");
        }
    }

    private void TogglePlay()
    {
        if (_media is null || _busy)
        {
            return;
        }

        if (_isPlaying)
        {
            SetPlaying(false);
            return;
        }

        // Playback owns the playhead again.
        _pinnedSeconds = null;

        if (Player.Position.TotalSeconds >= _media.Duration - 0.1)
        {
            SeekTo(0);
        }

        try
        {
            Player.Play();
            SetPlaying(true);
        }
        catch (Exception ex)
        {
            Log.Debug($"再生できません: {ex.Message}");
        }
    }

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;

        // Seeking a paused MediaElement still pushes a moment of audio, which made scrubbing the
        // timeline click. Sound is only wanted while playback actually runs.
        Player.IsMuted = !playing;

        if (!playing)
        {
            try
            {
                Player.Pause();
            }
            catch (Exception)
            {
                // Not opened yet.
            }
        }

        PlayGlyph.Text = playing ? "\uE769" : "\uE768";
    }

    private void FrameBack_Click(object sender, RoutedEventArgs e) => StepFrames(-1);

    private void FrameForward_Click(object sender, RoutedEventArgs e) => StepFrames(1);

    private void StepFrames(int delta, bool coarse = false)
    {
        if (_media is null || _busy)
        {
            return;
        }

        var step = (coarse ? 10 : 1) * _media.FrameSeconds;

        if (_edgeArmed && _selection is { } selection)
        {
            // Once an end has been grabbed (or a selection just made), the arrow keys nudge it.
            var start = selection.Start;
            var end = selection.End;

            if (_activeEdgeIsStart)
            {
                start = Math.Clamp(start + (delta * step), 0, Math.Max(0, end - MinSelectionSeconds));
            }
            else
            {
                end = Math.Clamp(
                    end + (delta * step),
                    Math.Min(_media.Duration, start + MinSelectionSeconds),
                    _media.Duration);
            }

            _selection = new TimeRange(start, end);
            UpdateFocusGeometry();
            UpdateSelectionUi();
            SeekTo(_activeEdgeIsStart ? start : end);
            return;
        }

        SeekTo(DisplaySeconds + (delta * step));
    }

    private void SeekTo(double seconds)
    {
        SetPlaying(false);

        // From here on the player is the truth again, so the playhead stops tracking the cursor.
        _playerAuthoritative = true;
        _pinnedSeconds = null;

        try
        {
            Player.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _media?.Duration ?? 0));
        }
        catch (Exception)
        {
            // Position is only settable once the media is open.
        }

        OnTick();
    }

    // ---------------------------------------------------------------- selection

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_cuts.RemoveLast())
        {
            UpdateKeptText();
            RedrawTimeline();
        }
    }

    private void ClearCuts_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _cuts.Clear();
        _selection = null;
        _edgeArmed = false;
        StatusText.Text = string.Empty;
        UpdateKeptText();
        RedrawTimeline();
    }

    private void UpdateKeptText()
    {
        if (_media is null)
        {
            return;
        }

        var kept = _cuts.KeptSeconds(_media.Duration);
        KeptText.Text = Loc.F("Trim.KeptValue", kept.ToString("0.0"), _media.Duration.ToString("0.0"));

        // Saving with nothing marked would only copy the whole clip again.
        ExportButton.IsEnabled = !_busy && kept > 0.5 && _cuts.Count > 0;
    }

    // ---------------------------------------------------------------- selection

    private void RemoveSelection_Click(object sender, RoutedEventArgs e) => RemoveSelection();

    private void RemoveSelection()
    {
        if (_busy || _selection is not { } selection || _media is null)
        {
            return;
        }

        if (selection.Duration >= 0.1)
        {
            _cuts.Add(selection);
            StatusText.Text = Loc.F("Trim.CutAdded", selection.Duration.ToString("0.0"), _cuts.Count.ToString());
        }

        _selection = null;
        UpdateKeptText();
        RedrawTimeline();
        UpdateSelectionUi();
    }

    /// <summary>Throws away everything except the selection: the "keep only the play" gesture.</summary>
    private void KeepOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selection is not { } selection || _media is null)
        {
            return;
        }

        _cuts.KeepOnly(selection, _media.Duration);
        _selection = null;
        StatusText.Text = Loc.F("Trim.KeptOnly", selection.Duration.ToString("0.0"));
        UpdateKeptText();
        RedrawTimeline();
        UpdateSelectionUi();
    }

    private void Invert_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _media is null || _cuts.Count == 0)
        {
            return;
        }

        _cuts.Invert(_media.Duration);
        _selection = null;
        UpdateKeptText();
        RedrawTimeline();
        UpdateSelectionUi();
    }

    private void UpdateSelectionUi()
    {
        if (_selection is { } selection && selection.Duration > 0.02)
        {
            RemoveSelectionButton.IsEnabled = !_busy;
            KeepOnlyButton.IsEnabled = !_busy;

            // The "adjusting …" wording is only for pointing at an end; the amber highlight
            // (which also covers the armed end) carries the state otherwise.
            var pointed = _edgeDrag ? (_edgeIsStart ? -1 : 1) : _hoverEdge;
            SelectionText.Text = pointed switch
            {
                -1 => Loc.F("Trim.EdgeStart", MMss(selection.Start)),
                1 => Loc.F("Trim.EdgeEnd", MMss(selection.End)),
                _ => Loc.F(
                    "Trim.SelectionValue",
                    MMss(selection.Start),
                    MMss(selection.End),
                    selection.Duration.ToString("0.0"))
            };

            return;
        }

        RemoveSelectionButton.IsEnabled = false;
        KeepOnlyButton.IsEnabled = false;
        SelectionText.Text = Loc.T("Trim.SelectHint");
    }

    // ---------------------------------------------------------------- export

    private void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The choice is per window; nothing to persist yet.
    }

    private async void Export_Click(object sender, RoutedEventArgs e) => await ExportAsync().ConfigureAwait(true);

    /// <summary>Dev hook: runs the export without a click so the UI path can be verified.</summary>
    internal async Task<string> ExportForTestAsync(bool keepOnly = false)
    {
        await _loadedSignal.Task.ConfigureAwait(true);
        if (keepOnly)
        {
            KeepOnly_Click(this, new RoutedEventArgs());
        }

        await ExportAsync().ConfigureAwait(true);
        return StatusText.Text;
    }

    private async Task ExportAsync()
    {
        if (_media is null || _busy)
        {
            return;
        }

        var keeps = _cuts.Keeps(_media.Duration);
        if (keeps.Count == 0)
        {
            StatusText.Text = Loc.T("Trim.Empty");
            return;
        }

        var name = FileNameBox.Text.Trim();
        if (name.Length == 0)
        {
            name = Path.GetFileNameWithoutExtension(_clip.FileName) + "_cut.mp4";
        }

        if (!name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            name += ".mp4";
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(_clip.Path)) ?? ".";

        // Never overwrite: a second save of the same clip gets "_1", "_2", ...
        var output = MakeUniquePath(Path.Combine(directory, name));
        FileNameBox.Text = Path.GetFileName(output);

        var context = new TrimContext(
            _clip.Path,
            output,
            TrimJob.CreateTempDirectory(output, AppPaths.TempDir),
            keeps,
            _keyframes,
            ModeBox.SelectedIndex == 1 ? CutMode.Fast : CutMode.Exact,
            _media.Fps,
            _media.HasAudio);

        SetBusy(true);
        ProgressBar.Value = 0;
        StatusText.Text = Loc.T("Trim.Exporting");

        try
        {
            var progress = new Progress<double>(value => ProgressBar.Value = value * 100);
            var outcome = await TrimJob.RunAsync(_ffmpegPath, context, progress).ConfigureAwait(true);

            if (!outcome.Success)
            {
                StatusText.Text = Loc.F("Trim.Failed", outcome.Error ?? "?");
                return;
            }

            ProgressBar.Value = 100;
            var sizeMb = new FileInfo(outcome.OutputPath).Length / 1048576.0;
            StatusText.Text = outcome.Warnings.Count == 0
                ? Loc.F("Trim.Done", outcome.DurationSeconds.ToString("0.0"), sizeMb.ToString("0.0"))
                : Loc.F("Trim.DoneWarn", outcome.Warnings[0]);

            _onSaved?.Invoke(outcome.OutputPath, outcome.DurationSeconds);
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.F("Trim.Failed", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PlayButton.IsEnabled = !busy;
        ModeBox.IsEnabled = !busy;
        FileNameBox.IsEnabled = !busy;
        UpdateSelectionUi();

        if (_media is not null)
        {
            ExportButton.IsEnabled = !busy && _cuts.KeptSeconds(_media.Duration) > 0.5 && _cuts.Count > 0;
        }
        else
        {
            ExportButton.IsEnabled = false;
        }
    }

    // ---------------------------------------------------------------- window

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>Escape and the close button must not throw away a cut list without asking.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_confirmedClose && !_busy && _cuts.Count > 0)
        {
            var answer = MessageBox.Show(
                this,
                Loc.T("Trim.ConfirmDiscard"),
                Loc.T("Trim.Title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _confirmedClose = true;
        }

        base.OnClosing(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        // Typing a file name must not toggle playback or drop a mark.
        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        if (_busy)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                TogglePlay();
                e.Handled = true;
                break;
            case Key.Left:
                StepFrames(-1, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                break;
            case Key.Right:
                StepFrames(1, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                break;
            case Key.Delete:
                RemoveSelection();
                e.Handled = true;
                break;
        }
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _mediaReady = true;
        _previewWarned = false;
        Log.Info($"プレビュー準備完了: {_clip.FileName}");
        BusyText.Visibility = Visibility.Collapsed;
        SetPlaying(false);
        try
        {
            // Nudge the position so the first frame is actually rendered.
            Player.Position = TimeSpan.FromMilliseconds(1);
        }
        catch (Exception)
        {
            // Ignore.
        }

        OnTick();
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _mediaReady = false;
        _previewWarned = true;
        Log.Warn($"プレビューできません: {e.ErrorException?.Message}");
        BusyText.Text = Loc.F("Trim.NoPreview", e.ErrorException?.Message ?? "?");
        BusyText.Visibility = Visibility.Visible;
    }

    /// <summary>Returns <paramref name="path"/>, or the first free "name_N.ext" next to it.</summary>
    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{name}_{index}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}_{Guid.NewGuid():N}{extension}");
    }

    private static string MMss(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds))
        {
            seconds = 0;
        }

        var span = TimeSpan.FromSeconds(seconds);
        var fraction = span.Milliseconds / 10;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}.{fraction:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}.{fraction:00}";
    }
}
