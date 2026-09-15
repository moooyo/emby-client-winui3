using Microsoft.UI.Xaml;

namespace EmbyClient.App;

public sealed partial class MainPage
{
    private void UpdateDirectSignInLayout()
    {
        if (DirectSignInHost is null || CredentialsForm is null || ManualSignIn is null) return;
        var direct = SavedAccounts.Items.Count == 0;
        if (direct && !ReferenceEquals(DirectSignInHost.Content, CredentialsForm))
        {
            ManualSignIn.Content = null;
            DirectSignInHost.Content = CredentialsForm;
        }
        else if (!direct && !ReferenceEquals(ManualSignIn.Content, CredentialsForm))
        {
            DirectSignInHost.Content = null;
            ManualSignIn.Content = CredentialsForm;
        }
        DirectSignInHost.Visibility = direct ? Visibility.Visible : Visibility.Collapsed;
        ManualSignIn.Visibility = direct ? Visibility.Collapsed : Visibility.Visible;
    }
}
