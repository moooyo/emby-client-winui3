using EmbyClient.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private Control? _normalPlayerFocus;
    private FocusState _normalPlayerFocusState;
    private int _compactFocusEpoch;
    private bool _compactHasProgress;
    private CompactOverlayLayout _compactLayout;

    private void InitializeCompactOverlay()
    {
        TitleText.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdateCompactTitle());
        EpisodeIdentityText.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdateCompactTitle());
        PauseButton.RegisterPropertyChangedCallback(ContentControl.ContentProperty, (_, _) => UpdateCompactCommandIcons());
        Timeline.RegisterPropertyChangedCallback(RangeBase.ValueProperty, (_, _) => UpdateCompactClock());
        Loaded += (_, _) =>
        {
            UpdateCompactLabels();
            UpdateCompactTitle();
            UpdateCompactClock();
        };
        Unloaded += (_, _) => InvalidateCompactFocus();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility != Visibility.Visible) InvalidateCompactFocus();
        });
        UpdateCompactLabels();
    }

    private void UpdateCompactLabels()
    {
        SetControlLabel(CompactRestoreButton,
            LumenText.Localize("Return to full window", "\u8fd4\u56de\u5b8c\u6574\u7a97\u53e3"));
        SetControlLabel(CompactStopButton,
            LumenText.Localize("Stop playback", "\u505c\u6b62\u64ad\u653e"));
    }

    private void UpdateCompactTitle()
    {
        if (CompactTitleText is null) return;
        CompactTitleText.Text = _item is { } item ? PlaybackTitle(item, TitleText.Text) : TitleText.Text;
        AutomationProperties.SetName(CompactTitleText, CompactTitleText.Text);
    }

    private void UpdateCompactClock()
    {
        if (CompactElapsedText is null) return;
        var context = _coordinator?.ActiveContext;
        var ended = _endedClockPresentation is { } candidate && _coordinator?.Status == EmbyClient.Playback.PlaybackStatus.Ended
            && candidate.ItemId == _item?.Id && candidate.Ticket.Intent == _playIntent
            && _notificationOwner.IsCurrent(candidate.Ticket) ? candidate : null;
        var position = context?.PositionTicks ?? ended?.PositionTicks ?? 0;
        var duration = context?.Source.RunTimeTicks ?? ended?.DurationTicks;
        if (context is not null && (_timelineDrag.IsActive || _keyboardTimelinePlaybackId == context.PlaybackId))
        {
            var preview = Timeline.Value * TimeSpan.TicksPerSecond;
            if (double.IsFinite(preview) && preview >= 0)
                position = preview >= long.MaxValue ? long.MaxValue : (long)preview;
        }
        var clock = CompactOverlayPresentationPolicy.GetClock(position, duration);
        CompactElapsedText.Text = FormatTime(clock.PositionTicks);
        CompactDurationText.Text = clock.DurationTicks is { } total ? FormatTime(total) : LumenText.Get("Live");
        CompactThinProgress.Value = clock.Progress;
        _compactHasProgress = clock.HasProgress;
        UpdateCompactProgressVisibility();
    }

    private void UpdateCompactProgressVisibility()
    {
        if (CompactThinProgress is null) return;
        CompactThinProgress.Visibility = _compactOverlay && _compactHasProgress ? Visibility.Visible : Visibility.Collapsed;
        CompactThinProgress.Opacity = AreControlsVisible ? 0 : 1;
    }

    private void CompactOverlayMeasured(object sender, SizeChangedEventArgs args)
    {
        if (!_compactOverlay) return;
        UpdateCompactOverlayLayout();
        UpdateSubtitleOverlayLayout();
        QueueNativeSubtitleImageLayout();
    }

    private void UpdateCompactOverlayLayout()
    {
        if (!_compactOverlay || CompactHeader is null || PlayerMain.ActualHeight <= 0) return;
        var headerBottom = Math.Max(44, CompactHeader.Margin.Top + CompactHeader.ActualHeight);
        var footerHeight = Math.Max(28, Math.Max(TimelineHost.ActualHeight,
            Math.Max(CompactElapsedHost.ActualHeight, CompactDurationHost.ActualHeight)));
        _compactLayout = CompactOverlayPresentationPolicy.CalculateLayout(PlayerMain.ActualHeight, headerBottom, footerHeight);
        TransportRow.Margin = new Thickness(0, _compactLayout.TransportTop, 0, 0);
    }

    private double CompactSubtitleBottom => _compactLayout.SubtitleBottom > 0 ? _compactLayout.SubtitleBottom : 50;

    private void UpdateCompactCommandIcons()
    {
        SetCommandIconSize(PauseButton, _compactOverlay ? 18 : 24);
        SetCommandIconSize(RewindButton, _compactOverlay ? 19 : 24);
        SetCommandIconSize(ForwardButton, _compactOverlay ? 19 : 24);
    }

    private static void SetCommandIconSize(Button button, double size)
    {
        if (button.Content is not FrameworkElement icon) return;
        icon.Width = icon.Height = size;
    }

    private void PrepareCompactFocus(bool entering)
    {
        ++_compactFocusEpoch;
        if (!entering) return;
        _normalPlayerFocus = XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is Control focused
            && IsWithin(focused, PlayerRoot) ? focused : null;
        _normalPlayerFocusState = _normalPlayerFocus?.FocusState ?? FocusState.Programmatic;
    }

    private void RestoreCompactTransitionFocus(bool entering)
    {
        var epoch = _compactFocusEpoch;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsLoaded || Visibility != Visibility.Visible || _compactOverlay != entering || epoch != _compactFocusEpoch) return;
            var target = entering ? PauseButton : _normalPlayerFocus;
            var state = _normalPlayerFocusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;
            if (target is null || !target.IsLoaded || !target.IsEnabled || !target.Focus(state))
                VideoFocusTarget.Focus(state);
            if (!entering) _normalPlayerFocus = null;
        });
    }

    private void InvalidateCompactFocus()
    {
        ++_compactFocusEpoch;
        _normalPlayerFocus = null;
    }
}
