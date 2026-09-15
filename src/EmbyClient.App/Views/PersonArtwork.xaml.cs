using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;

namespace EmbyClient.App.Views;

public sealed partial class PersonArtwork : UserControl
{
    private Func<MediaCardViewModel, int, int, CancellationToken, Task<byte[]?>>? _loader;
    private CancellationTokenSource? _request;
    private MediaCardViewModel? _displayed;

    public PersonArtwork()
    {
        InitializeComponent();
        SizeChanged += (_, args) => UpdatePortraitSize(args.NewSize.Width, args.NewSize.Height);
        Loaded += async (_, _) =>
        {
            UpdatePortraitSize(ActualWidth, ActualHeight);
            if (Item is { } item)
            {
                item.PropertyChanged -= Item_PropertyChanged;
                item.PropertyChanged += Item_PropertyChanged;
            }
            await RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            if (Item is { } item) item.PropertyChanged -= Item_PropertyChanged;
            Reset();
        };
    }

    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item),
        typeof(MediaCardViewModel), typeof(PersonArtwork), new PropertyMetadata(null, ItemChanged));
    public MediaCardViewModel? Item
    {
        get => (MediaCardViewModel?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public static readonly DependencyProperty IsPersonProperty = DependencyProperty.Register(nameof(IsPerson),
        typeof(bool), typeof(PersonArtwork), new PropertyMetadata(true, IsPersonChanged));
    public bool IsPerson
    {
        get => (bool)GetValue(IsPersonProperty);
        set => SetValue(IsPersonProperty, value);
    }

    internal async void SetImageLoader(Func<MediaCardViewModel, int, int, CancellationToken, Task<byte[]?>> loader)
    {
        if (_loader != loader) Reset();
        _loader = loader;
        await RefreshAsync();
    }

    private static async void ItemChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var artwork = (PersonArtwork)sender;
        if (args.OldValue is MediaCardViewModel previous) previous.PropertyChanged -= artwork.Item_PropertyChanged;
        if (artwork.IsLoaded && artwork.Item is { } current) current.PropertyChanged += artwork.Item_PropertyChanged;
        artwork.Reset();
        artwork.Portrait.DisplayName = artwork.Item?.Title ?? string.Empty;
        await artwork.RefreshAsync();
    }

    private async void Item_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(MediaCardViewModel.Item) or null or "")) return;
        Reset();
        Portrait.DisplayName = Item?.Title ?? string.Empty;
        await RefreshAsync();
    }

    private static void IsPersonChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var artwork = (PersonArtwork)sender;
        artwork.Portrait.Visibility = artwork.IsPerson ? Visibility.Visible : Visibility.Collapsed;
        artwork.PosterFrame.Visibility = artwork.IsPerson ? Visibility.Collapsed : Visibility.Visible;
        artwork.UpdatePortraitSize(artwork.ActualWidth, artwork.ActualHeight);
    }

    private void UpdatePortraitSize(double width, double height)
    {
        if (!IsPerson || width <= 0 || height <= 0) return;
        var diameter = Math.Min(width, height);
        Portrait.Width = diameter;
        Portrait.Height = diameter;
    }

    private async Task RefreshAsync()
    {
        if (!IsLoaded || Item is not { } item || _loader is null || ReferenceEquals(_displayed, item)) return;
        Reset();
        _displayed = item;
        var source = new CancellationTokenSource();
        var token = source.Token;
        _request = source;
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var width = (int)Math.Clamp(Math.Max(48, ActualWidth) * scale, 48, 432);
        var height = (int)Math.Clamp(Math.Max(48, ActualHeight) * scale, 48, 648);
        try
        {
            var bytes = await _loader(item, width, height, token);
            if (bytes is not { Length: > 0 } || token.IsCancellationRequested) return;
            await NativePosterDecoder.DecodeAndApplyAsync(bytes, width, height, token, bitmap =>
            {
                if (!token.IsCancellationRequested && IsLoaded && ReferenceEquals(Item, item) && ReferenceEquals(_request, source))
                {
                    Portrait.ProfilePicture = bitmap;
                    Poster.Source = bitmap;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // The owner also links account and dialog lifetime tokens to this request.
        }
        catch (Exception exception) when (exception is EmbyApiException or EmbyTransportException or EmbyProtocolException
            or TimeoutException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // Missing artwork keeps the native initials or media placeholder.
        }
        finally
        {
            if (ReferenceEquals(_request, source))
            {
                _request = null;
                source.Dispose();
            }
        }
    }

    private void Reset()
    {
        var previous = _request;
        _request = null;
        previous?.Cancel();
        previous?.Dispose();
        _displayed = null;
        Portrait.ProfilePicture = null;
        Poster.Source = null;
    }
}
