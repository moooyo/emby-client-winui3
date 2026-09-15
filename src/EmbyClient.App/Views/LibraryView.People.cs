using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;

namespace EmbyClient.App.Views;

public sealed partial class LibraryView
{
    private PersonDetailsView? _personPage;
    public bool IsPersonDialogOpen => false;

    private void ClosePersonDialog()
    {
        if (_personPage is not { } page) return;
        page.WorkInvoked -= PersonWorkInvoked;
        page.Detach();
        _personPage = null;
        PersonPageHost.Content = null;
    }

    private void UpdatePersonPage()
    {
        if (!_componentInitialized) return;
        var person = ViewModel.IsPerson ? ViewModel.ActivePerson : null;
        if (ReferenceEquals(_personPage?.ViewModel, person)) return;
        ClosePersonDialog();
        if (person is not null)
        {
            var page = new PersonDetailsView(person);
            page.WorkInvoked += PersonWorkInvoked;
            _personPage = page;
            PersonPageHost.Content = page;
        }
        NavigationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CapturePersonPageState() => _personPage?.CaptureState();

    private void FocusPersonDestination()
    {
        UpdatePersonPage();
        _personPage?.FocusDestination();
    }

    private void Cast_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is CastSection section) section.Library = ViewModel;
    }

    private async void Cast_PersonInvoked(object? sender, PersonInvokedEventArgs args)
    {
        var navigation = ViewModel.ShowPersonAsync(args.Person);
        var person = ViewModel.ActivePerson;
        if (person is not null)
        {
            UpdateNavigation();
            FocusPersonDestination();
        }
        await navigation;
        if (!ViewModel.IsPerson || person is null || !ReferenceEquals(ViewModel.ActivePerson, person)) return;
        UpdatePersonPage();
        UpdateNavigation();
    }

    private async void PersonWorkInvoked(object? sender, MediaCardViewModel work)
    {
        if (sender is not PersonDetailsView page || !ReferenceEquals(page, _personPage)
            || !ViewModel.IsPerson || !ReferenceEquals(ViewModel.ActivePerson, page.ViewModel)) return;
        page.CaptureState();
        await ViewModel.ShowItemAsync(work);
        if (!ViewModel.HasDetails || ViewModel.Detail.Id != work.Id) return;
        UpdatePersonPage();
        UpdateNavigation();
        FocusCurrentDetails();
    }
}
