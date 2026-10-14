using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private static void RegisterCatalogText() => LumenText.Register(new Dictionary<string, string>
    {
        ["Random"] = "\u968f\u673a",
        ["{0} media titles"] = "{0} \u90e8",
        ["{0} favorite items"] = "{0} \u9879",
        ["Release date"] = "\u4e0a\u6620\u65e5\u671f",
        ["Video quality"] = "\u753b\u8d28",
        ["Clear all filters"] = "\u6e05\u9664\u5168\u90e8\u7b5b\u9009",
        ["Poster width: {0} px"] = "\u6d77\u62a5\u5bbd\u5ea6\uff1a{0} px",
        ["No items match your filters"] = "\u6ca1\u6709\u7b26\u5408\u6761\u4ef6\u7684\u5185\u5bb9",
        ["Favorite a movie or series to find it here."] = "\u5728\u5f71\u7247\u6216\u5267\u96c6\u4e0a\u70b9\u6536\u85cf\uff0c\u6216\u5728\u53f3\u952e\u83dc\u5355\u91cc\u9009\u62e9\u300c\u6536\u85cf\u300d\uff0c\u5b83\u5c31\u4f1a\u51fa\u73b0\u5728\u8fd9\u91cc\u3002",
        ["Try a different genre or clear your filters."] = "\u8bd5\u8bd5\u6362\u4e00\u4e2a\u7c7b\u578b\uff0c\u6216\u6e05\u9664\u7b5b\u9009\u6761\u4ef6\u3002",
        ["Refresh your library or browse another collection."] = "\u5237\u65b0\u5a92\u4f53\u5e93\uff0c\u6216\u6d4f\u89c8\u5176\u4ed6\u5a92\u4f53\u5e93\u3002",
        ["Browse movies"] = "\u6d4f\u89c8\u7535\u5f71",
        ["Back to home"] = "\u56de\u5230\u9996\u9875",
        ["Search movies, series, people, or genres"] = "\u641c\u7d22\u7535\u5f71\u3001\u5267\u96c6\u3001\u4eba\u7269\u6216\u7c7b\u578b",
        ["No results for \u201c{0}\u201d"] = "\u6ca1\u6709\u627e\u5230\u4e0e\u201c{0}\u201d\u76f8\u5173\u7684\u5185\u5bb9",
        ["Try a person, an original title, or a genre."] = "\u8bd5\u8bd5\u6f14\u5458\u540d\u3001\u82f1\u6587\u7247\u540d\u6216\u7c7b\u578b\u3002",
        ["Searching..."] = "\u6b63\u5728\u641c\u7d22\u2026",
        ["Search all types"] = "\u641c\u7d22\u5168\u90e8\u7c7b\u578b"
    });

    private void ResetCatalogPresentation()
    {
        ResetSearchDiscovery();
        _wallContext = _wallEmptySignature = string.Empty;
        _wallLibrarySignature = string.Empty;
        _wallLibraryInputVersion++;
        _updatingWallLibraries = true;
        _wallLibrarySelector.Items.Clear();
        _wallLibraryOptions.Clear();
        _updatingWallLibraries = false;
        _wallLibraryStrip.Visibility = Visibility.Collapsed;
        _wallModes.Clear();
        _wallEmpty.Children.Clear();
        _searchSummarySignature = string.Empty;
        _searchPeopleRenderQuery = string.Empty;
        _searchLayoutKey = null;
        _searchTotalsLifetime?.Cancel();
        _searchTotalsLifetime?.Dispose();
        _searchTotalsLifetime = null;
        _searchTotalsRequestQuery = string.Empty;
        _searchResults.Children.Clear();
        _searchFeatured.Children.Clear();
        _searchSecondary.Children.Clear();
    }

    private sealed partial class CatalogSkeletonFactory(Func<double> width) : IElementFactory
    {
        public UIElement GetElement(ElementFactoryGetArgs args) => new CatalogSkeletonCard(width(), args.Data is int index ? index : 0);
        public void RecycleElement(ElementFactoryRecycleArgs args) { }
    }

    private sealed partial class CatalogSkeletonCard : UserControl
    {
        private readonly Border _poster = new() { CornerRadius = new CornerRadius(12), Background = LumenTheme.Brush("Skeleton") };
        private readonly Border _title = new() { Height = 12, Margin = new Thickness(0, 12, 0, 0), CornerRadius = new CornerRadius(6), Background = LumenTheme.Brush("Skeleton"), HorizontalAlignment = HorizontalAlignment.Left };
        private readonly Border _subtitle = new() { Height = 10, Margin = new Thickness(0, 8, 0, 0), CornerRadius = new CornerRadius(5), Background = LumenTheme.Brush("Skeleton"), HorizontalAlignment = HorizontalAlignment.Left };
        private readonly double _baseOpacity;
        private Storyboard? _pulse;

        public CatalogSkeletonCard(double width, int index)
        {
            _baseOpacity = 1 - index % 8 * .06;
            IsHitTestVisible = false;
            IsTabStop = false;
            var content = new StackPanel { Opacity = _baseOpacity };
            content.Children.Add(_poster);
            content.Children.Add(_title);
            content.Children.Add(_subtitle);
            Content = content;
            SetWidth(width);
            Loaded += (_, _) =>
            {
                if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
                var animation = new DoubleAnimation
                {
                    From = _baseOpacity, To = _baseOpacity * .52, Duration = TimeSpan.FromMilliseconds(700),
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                Storyboard.SetTarget(animation, content);
                Storyboard.SetTargetProperty(animation, "Opacity");
                _pulse = new Storyboard();
                _pulse.Children.Add(animation);
                _pulse.Begin();
            };
            Unloaded += (_, _) => { _pulse?.Stop(); _pulse = null; };
        }

        public void SetWidth(double width)
        {
            Width = width;
            _poster.Height = width * 1.5;
            _title.Width = width * .72;
            _subtitle.Width = width * .44;
        }
    }
}
