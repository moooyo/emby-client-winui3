using EmbyClient.Api;

namespace EmbyClient.App.ViewModels;

public sealed partial class LibraryViewModel
{
    public PersonDetailsViewModel? CreatePersonDetails(PersonCardViewModel person)
    {
        if (!person.CanOpen || _api is null || _user?.Id is null || _sessionCancellation is null) return null;
        var version = _sessionVersion;
        return new(person, Detail.Title, _api, _images, _serverId, _user.Id, _sessionCancellation.Token, exception =>
        {
            if (IsCurrentSession(version) && IsSessionExpired(exception)) ShowError(exception);
        });
    }
}
