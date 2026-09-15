using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyClient.App.Views;

public sealed partial class MediaStreamGroupView : UserControl
{
    public MediaStreamGroupView() => InitializeComponent();

    public static readonly DependencyProperty GroupProperty = DependencyProperty.Register(nameof(Group),
        typeof(MediaStreamGroupViewModel), typeof(MediaStreamGroupView), new PropertyMetadata(null, GroupChanged));

    public MediaStreamGroupViewModel? Group
    {
        get => (MediaStreamGroupViewModel?)GetValue(GroupProperty);
        set => SetValue(GroupProperty, value);
    }

    private static void GroupChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((MediaStreamGroupView)sender).Bindings.Update();

    private void ToggleTracks_Click(object sender, RoutedEventArgs args)
    {
        if (Group is { } group) group.IsExpanded = !group.IsExpanded;
    }
}
