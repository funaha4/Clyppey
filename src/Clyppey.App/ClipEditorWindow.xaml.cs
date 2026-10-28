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
    private double? _markStart;
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
    private Brush _accentBrush = Brushes.Aquamarine;
    private Brush _warnBrush = Brushes.Gold;
    private const int PreviewSeekIntervalMs = 110;
    private const double MinSelectionSeconds = 0.1;
    private const double EdgeHitPixels = 6;
    private const double ClickSlopPixels = 4;

    private DateTime _lastHoverSeekUtc = DateTime.MinValue;
    private double _lastHoverSeconds = -1;
    private bool _isPlaying;
    private bool _busy;
    private bool _mediaReady;
    private bool _previewWarned;
    private DateTime _openedAtUtc = DateTime.UtcNow;

    public ClipEditorWindow(
        ClipEntry clip,
        string ffmpegPath,
        Action<string, double>? onSaved = null,
        IReadOnlyList<TimeRange>? initialCuts = null,
        TimeRange? initialSelection = null,
        double? initialPin = null)
    {
        InitializeComponent();

        _clip = clip;
        _ffmpegPath = ffmpegPath;
        _onSaved = onSaved;
        _initialCuts = initialCuts;
        _initialSelection = initialSelection;
        _initialPin = initialPin;

        ClipNameText.Text = clip.FileName;
        FileNameBox.Text = Path.GetFileNameWithoutExtension(clip.FileName) + "_cut.mp4";
        _accentBrush = (Brush)FindResource("AccentBrush");
        _warnBrush = (Brush)FindResource("WarnBrush");

        ModeBox.Items.Add(Loc.T("Trim.ModeExact"));
        ModeBox.Items.Add(Loc.T("Trim.ModeFast"));
        ModeBox.SelectedIndex = 0;
        UpdateSelectionUi();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

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

    /// <summary>
    /// Where the playhead should be drawn, and where a mark lands.
    ///
    /// While the pointer is on the timeline the answer is the pointer, not the player: seeking
    /// is throttled and Media Foundation finishes it asynchronously, so following the player
    /// made the playhead step along at about 9 Hz while the cursor moved smoothly.
    /// </summary>
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
            UpdateKeptText();
            RedrawTimeline();

            _keyframes = await KeyframeIndex.ReadAsync(_ffmpegPath, _clip.Path).ConfigureAwait(true);

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

            await LoadFilmstripAsync().ConfigureAwait(true);
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
    /// One image strip, one thumbnail per second, so the kills can be spotted without
    /// scrubbing. Generated on demand and deleted right after it is loaded into memory.
    /// </summary>
    private async Task LoadFilmstripAsync()
    {
        if (_media is null)
        {
            return;
        }

        var count = Math.Clamp((int)Math.Ceiling(_media.Duration), 1, 120);
        var rate = (count / Math.Max(_media.Duration, 0.1)).ToString("0.######", CultureInfo.InvariantCulture);

        Directory.CreateDirectory(AppPaths.TempDir);
        var file = Path.Combine(AppPaths.TempDir, "strip_" + Guid.NewGuid().ToString("N")[..8] + ".png");

        var args = new[]
        {
            "-y", "-v", "error",
            "-i", _clip.Path,
            "-vf", $"fps={rate},scale=160:-2,tile={count}x1",
            "-frames:v", "1",
            file
        };

        var (success, error) = await ProcessRunner.RunAsync(_ffmpegPath, args, 30_000).ConfigureAwait(true);
        if (!success || !File.Exists(file))
        {
            Log.Debug($"フィルムストリップを作れません: {error}");
            return;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(file);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        FilmstripImage.Source = bitmap;

        try
        {
            File.Delete(file);
        }
        catch (Exception)
        {
            // A leftover temp image is harmless.
        }
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

        var duration = _media.Duration;

        // Keyframe ticks: the only places a copy-mode cut can start.
        foreach (var keyframe in _keyframes)
        {
            var tick = new Border
            {
                Width = 1,
                Height = height * 0.18,
                Background = new SolidColorBrush(Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF))
            };
            Canvas.SetLeft(tick, keyframe / duration * width);
            Canvas.SetTop(tick, height * 0.82);
            TimelineCanvas.Children.Add(tick);
        }

        foreach (var range in _cuts.Removed)
        {
            var boxWidth = Math.Max(3, range.Duration / duration * width);
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

            Canvas.SetLeft(box, range.Start / duration * width);
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
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x5E, 0xEA, 0xD8)),
            Visibility = Visibility.Collapsed
        };
        var focusRight = new Border
        {
            Width = 2,
            Height = height,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x5E, 0xEA, 0xD8)),
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
        Canvas.SetLeft(playhead, CurrentSeconds / duration * width);
        TimelineCanvas.Children.Add(playhead);
        _playhead = playhead;
    }

    /// <summary>Whatever the user is currently pointing at: a dragged selection or an open mark.</summary>
    private TimeRange? FocusRange
    {
        get
        {
            if (_selection is { } selection && selection.Duration > 0.01)
            {
                return selection;
            }

            if (_markStart is { } start)
            {
                var range = TimeRange.Normalized(start, DisplaySeconds);
                return range.Duration > 0.01 ? range : null;
            }

            return null;
        }
    }

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

        var from = band.Start / _media.Duration * width;
        var to = band.End / _media.Duration * width;
        var pixels = Math.Max(1, to - from);

        _focusFill.Visibility = Visibility.Visible;
        _focusFill.Width = pixels;
        Canvas.SetLeft(_focusFill, from);

        _focusLeft.Visibility = Visibility.Visible;
        Canvas.SetLeft(_focusLeft, from);

        // Two edges on top of each other just reads as a thicker line, so one is enough.
        if (pixels < 6)
        {
            _focusRight.Visibility = Visibility.Collapsed;
        }
        else
        {
            _focusRight.Visibility = Visibility.Visible;
            Canvas.SetLeft(_focusRight, to - 2);
        }

        // Handles only exist for a real selection; the I/O preview range has nothing to grab.
        var grabbable = _selection is not null;
        var top = Math.Max(0, (TimelineCanvas.ActualHeight - 16) / 2);

        if (_handleLeft is not null)
        {
            _handleLeft.Visibility = grabbable ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(_handleLeft, from - 4);
            Canvas.SetTop(_handleLeft, top);
        }

        if (_handleRight is not null)
        {
            _handleRight.Visibility = grabbable ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(_handleRight, to - 4);
            Canvas.SetTop(_handleRight, top);
        }
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

        var tolerance = EdgeHitPixels / width * _media.Duration;
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

        Canvas.SetLeft(_playhead, Math.Clamp(seconds / _media.Duration, 0, 1) * width);
    }

    private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawTimeline();

    private double SecondsAt(Point point)
    {
        if (_media is null)
        {
            return 0;
        }

        var width = TimelineCanvas.ActualWidth;
        if (width <= 0)
        {
            return 0;
        }

        return Math.Clamp(point.X / width, 0, 1) * _media.Duration;
    }

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
                TimelineCanvas.Cursor = HitTestEdge(pointer) != 0 ? Cursors.SizeWE : Cursors.Hand;
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
            var slop = ClickSlopPixels / Math.Max(1.0, TimelineCanvas.ActualWidth) * _media.Duration;
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

        if (_isPlaying && Player.Position.TotalSeconds >= _media.Duration - 0.05)
        {
            SetPlaying(false);
        }

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

    // ---------------------------------------------------------------- marking

    private void MarkStart_Click(object sender, RoutedEventArgs e) => SetMarkStart();

    private void MarkEnd_Click(object sender, RoutedEventArgs e) => CloseMark();

    private void SetMarkStart()
    {
        if (_media is null || _busy)
        {
            return;
        }

        _markStart = DisplaySeconds;
        StatusText.Text = Loc.F("Trim.Marking", MMss(_markStart.Value));
        RedrawTimeline();
    }

    private void CloseMark()
    {
        if (_media is null || _busy)
        {
            return;
        }

        if (_markStart is null)
        {
            SetMarkStart();
            return;
        }

        var start = Math.Min(_markStart.Value, DisplaySeconds);
        var end = Math.Max(_markStart.Value, DisplaySeconds);
        _markStart = null;

        if (end - start >= 0.1)
        {
            _cuts.Add(new TimeRange(start, end));
            StatusText.Text = Loc.F("Trim.CutAdded", (end - start).ToString("0.0"), _cuts.Count.ToString());
        }

        UpdateKeptText();
        RedrawTimeline();
    }

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
        _markStart = null;
        _selection = null;
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
        ExportButton.IsEnabled = !_busy && kept > 0.5;
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
            SelectionText.Text = Loc.F(
                "Trim.SelectionValue",
                MMss(selection.Start),
                MMss(selection.End),
                selection.Duration.ToString("0.0"));
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
        var output = Path.Combine(directory, name);
        if (string.Equals(output, _clip.Path, StringComparison.OrdinalIgnoreCase))
        {
            output = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + "_1.mp4");
        }

        var context = new TrimContext(
            _clip.Path,
            output,
            Path.Combine(AppPaths.TempDir, "cut_" + Guid.NewGuid().ToString("N")[..8]),
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
        MarkStartButton.IsEnabled = !busy;
        MarkEndButton.IsEnabled = !busy;
        ModeBox.IsEnabled = !busy;
        FileNameBox.IsEnabled = !busy;
        UpdateSelectionUi();

        if (_media is not null)
        {
            ExportButton.IsEnabled = !busy && _cuts.KeptSeconds(_media.Duration) > 0.5;
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
            case Key.I:
                SetMarkStart();
                e.Handled = true;
                break;
            case Key.O:
                CloseMark();
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
