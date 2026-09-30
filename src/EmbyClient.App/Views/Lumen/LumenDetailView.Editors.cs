using EmbyClient.Api;
using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private Task AddToCollectionAsync()
    {
        var itemId = _library.Detail.Id;
        return RunDetailActionAsync(async (session, token) =>
        {
            SetNotice(LumenText.Get("Loading collections..."));
            var collections = new List<BaseItemDto>();
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            var offset = 0;
            while (true)
            {
                var page = await session.Api.GetItemsAsync(new ItemQuery
                {
                    IncludeItemTypes = ["BoxSet"], Recursive = true, Limit = 500, StartIndex = offset,
                    SortBy = ["SortName"], SortOrder = ["Ascending"], EnableImages = false
                }, token);
                var added = 0;
                foreach (var collection in page.Items)
                    if (!string.IsNullOrWhiteSpace(collection.Id) && identifiers.Add(collection.Id)) { collections.Add(collection); added++; }
                offset += page.Items.Length;
                if (added == 0 || page.Items.Length < 500 || page.TotalRecordCount is { } count && offset >= count) break;
            }
            token.ThrowIfCancellationRequested();
            if (_library.Detail.Id != itemId) return;
            SetNotice(string.Empty);
            var content = new StackPanel { Spacing = 16, MinWidth = 340 };
            var choice = new ComboBox { Header = LumenText.Get("Choose collection"), HorizontalAlignment = HorizontalAlignment.Stretch };
            choice.Items.Add(new ComboBoxItem { Content = LumenText.Get("New collection"), Tag = null });
            foreach (var collection in collections)
                choice.Items.Add(new ComboBoxItem { Content = collection.Name ?? LumenText.Get("Collection"), Tag = collection.Id, IsEnabled = collection.CanEditItems != false });
            var name = MetadataField("Collection name", string.Empty);
            content.Children.Add(choice);
            content.Children.Add(name);
            var dialog = CreateDialog("Add to collection", content, "Create");
            void UpdateSelection()
            {
                var create = choice.SelectedIndex <= 0;
                name.Visibility = create ? Visibility.Visible : Visibility.Collapsed;
                dialog.PrimaryButtonText = LumenText.Get(create ? "Create" : "Add");
                dialog.IsPrimaryButtonEnabled = !token.IsCancellationRequested && (create ? !string.IsNullOrWhiteSpace(name.Text) : choice.SelectedItem is ComboBoxItem { IsEnabled: true });
            }
            choice.SelectionChanged += (_, _) => UpdateSelection();
            name.TextChanged += (_, _) => UpdateSelection();
            choice.SelectedIndex = collections.Count > 0 ? 1 : 0;
            UpdateSelection();
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
            token.ThrowIfCancellationRequested();
            if (_library.Detail.Id != itemId) return;
            if (choice.SelectedItem is ComboBoxItem { Tag: string collectionId })
                await session.Api.AddToCollectionAsync(collectionId, [itemId], token);
            else await session.Api.CreateCollectionAsync(name.Text.Trim(), [itemId], cancellationToken: token);
            token.ThrowIfCancellationRequested();
            if (_library.Detail.Id == itemId) SetNotice(LumenText.Get("Saved to collection."));
        });
    }

    private Task EditMetadataAsync()
    {
        if (!CanEditMetadata()) return Task.CompletedTask;
        var item = _library.Detail.Item;
        return RunDetailActionAsync(async (session, token) =>
        {
            var fields = new StackPanel { Spacing = 14, MinWidth = 420 };
            var name = MetadataField("Name", item.Name);
            var original = MetadataField("Original title", item.OriginalTitle);
            var overview = MetadataField("Overview", item.Overview, true);
            var year = MetadataField("Production year", item.ProductionYear?.ToString(CultureInfo.InvariantCulture));
            var rating = MetadataField("Official rating", item.OfficialRating);
            var genres = MetadataField("Genres", string.Join(", ", item.Genres ?? []));
            var tags = MetadataField("Tags", string.Join(", ", item.Tags ?? []));
            var originalGenresText = genres.Text;
            var originalTagsText = tags.Text;
            var locked = new CheckBox { Content = LumenText.Get("Lock metadata"), IsChecked = item.LockData == true };
            var validation = LumenUi.Text(string.Empty, 13);
            validation.Foreground = LumenTheme.Brush("Danger");
            validation.Visibility = Visibility.Collapsed;
            foreach (var field in new[] { name, original, overview, year, rating, genres, tags }) fields.Children.Add(field);
            fields.Children.Add(locked);
            fields.Children.Add(validation);
            var scroll = new ScrollViewer { Content = fields, MaxHeight = Math.Max(180, Math.Min(560, (XamlRoot?.Size.Height ?? 900) - 220)),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var dialog = CreateDialog("Edit metadata", scroll, "Save");
            bool ValidYear() => string.IsNullOrWhiteSpace(year.Text) ? item.ProductionYear is null
                : int.TryParse(year.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= 9999;
            void Validate()
            {
                var error = string.IsNullOrWhiteSpace(name.Text) ? "Enter a name." : !ValidYear() ? "Enter a valid year." : null;
                validation.Text = error is null ? string.Empty : LumenText.Get(error);
                validation.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
                dialog.IsPrimaryButtonEnabled = error is null && !token.IsCancellationRequested;
            }
            name.TextChanged += (_, _) => Validate();
            year.TextChanged += (_, _) => Validate();
            Validate();
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
            token.ThrowIfCancellationRequested();
            if (_library.Detail.Id != item.Id || !CanEditMetadata()) return;
            var newYear = int.TryParse(year.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedYear) ? parsedYear : (int?)null;
            // Formatting a list is lossy for values containing separators; parse only edited fields.
            var newGenres = genres.Text == originalGenresText ? null : ParseMetadataList(genres.Text);
            var newTags = tags.Text == originalTagsText ? null : ParseMetadataList(tags.Text);
            await session.Api.UpdateItemMetadataAsync(item.Id!, new ItemMetadataUpdate
            {
                Name = name.Text.Trim() == item.Name ? null : name.Text.Trim(),
                OriginalTitle = original.Text.Trim() == (item.OriginalTitle ?? string.Empty) ? null : original.Text.Trim(),
                Overview = overview.Text == (item.Overview ?? string.Empty) ? null : overview.Text,
                ProductionYear = newYear == item.ProductionYear ? null : newYear,
                OfficialRating = rating.Text.Trim() == (item.OfficialRating ?? string.Empty) ? null : rating.Text.Trim(),
                Genres = newGenres is null || newGenres.SequenceEqual(item.Genres ?? [], StringComparer.Ordinal) ? null : newGenres,
                Tags = newTags is null || newTags.SequenceEqual(item.Tags ?? [], StringComparer.Ordinal) ? null : newTags,
                LockData = locked.IsChecked == (item.LockData == true) ? null : locked.IsChecked == true
            }, token);
            await _library.RefreshAsync(token);
            token.ThrowIfCancellationRequested();
            if (_library.Detail.Id == item.Id) SetNotice(LumenText.Get("Metadata saved."));
        });
    }

    private ContentDialog? _activeDialog;
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (XamlRoot is null) return ContentDialogResult.None;
        _activeDialog = dialog;
        try { return await dialog.ShowAsync(); }
        finally { if (ReferenceEquals(_activeDialog, dialog)) _activeDialog = null; }
    }

    private static string[] ParseMetadataList(string value) => value.Split([',', ';', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
