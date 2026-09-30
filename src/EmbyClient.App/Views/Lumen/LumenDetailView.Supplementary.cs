using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Net;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private bool _localTrailerFailed;
    private int _supplementaryRequestVersion;

    private async Task LoadSupplementaryAsync(string itemId, int version, CancellationToken cancellationToken)
    {
        var session = _session;
        if (session is null || !IsCurrentDetail(itemId, version)) return;
        var request = ++_supplementaryRequestVersion;
        var item = _library.Detail.Item;
        _supplementaryLoading = true;
        _similarFailed = false;
        _supplementaryRevision++;
        Refresh();
        var relatedId = item.Type is "Episode" or "Season" && !string.IsNullOrWhiteSpace(item.SeriesId) ? item.SeriesId : itemId;
        async Task ReadSimilarAsync()
        {
            try
            {
                var similar = await session.Api.GetSimilarItemsAsync(relatedId!, cancellationToken: cancellationToken);
                if (!IsCurrentDetail(itemId, version) || request != _supplementaryRequestVersion) return;
                _similarItems = similar.Items.Where(candidate => !string.IsNullOrWhiteSpace(candidate.Id)
                    && candidate.Id != itemId && candidate.Id != relatedId)
                    .DistinctBy(candidate => candidate.Id, StringComparer.Ordinal).Select(candidate => new MediaCardViewModel(candidate)).ToList();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (EmbyApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                if (IsCurrentDetail(itemId, version) && request == _supplementaryRequestVersion) _similarItems.Clear();
            }
            catch (Exception exception) when (ExpectedRequestFailure(exception))
            {
                if (!IsCurrentDetail(itemId, version) || request != _supplementaryRequestVersion) return;
                _similarFailed = true;
                if (exception is EmbyApiException { IsAuthenticationFailure: true }) SetNotice(DescribeRequestFailure(exception), true);
            }
        }
        async Task ReadTrailersAsync()
        {
            if (item.LocalTrailerCount == 0) return;
            try
            {
                var trailers = await session.Api.GetLocalTrailersAsync(itemId, cancellationToken);
                if (!IsCurrentDetail(itemId, version) || request != _supplementaryRequestVersion) return;
                _localTrailers = trailers.Where(candidate => new MediaCardViewModel(candidate).CanPlay).ToList();
                _localTrailerFailed = false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (EmbyApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                if (IsCurrentDetail(itemId, version) && request == _supplementaryRequestVersion)
                {
                    _localTrailers.Clear();
                    _localTrailerFailed = false;
                }
            }
            catch (Exception exception) when (ExpectedRequestFailure(exception))
            {
                if (IsCurrentDetail(itemId, version) && request == _supplementaryRequestVersion) _localTrailerFailed = true;
            }
        }
        try { await Task.WhenAll(ReadSimilarAsync(), ReadTrailersAsync()); }
        finally
        {
            if (IsCurrentDetail(itemId, version) && request == _supplementaryRequestVersion)
            {
                _supplementaryLoading = false;
                _supplementaryRevision++;
                Refresh();
            }
        }
    }

    private IEnumerable<(string Name, Uri Uri)> RemoteTrailerUris()
    {
        foreach (var trailer in _library.Detail.Item.RemoteTrailers ?? [])
            if (Uri.TryCreate(trailer.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo))
                yield return (trailer.Name ?? LumenText.Get("Remote trailer"), uri);
    }

    private async Task PlayTrailerAsync()
    {
        if (_session is null || !_library.IsPlaybackAllowed || _actionBusy || _detailCancellation is null || _detailId is null) return;
        var itemId = _detailId;
        var version = _detailVersion;
        var token = _detailCancellation.Token;
        if (_localTrailerFailed)
        {
            _actionBusy = true;
            UpdateActionState();
            try { await LoadSupplementaryAsync(itemId, version, token); }
            finally
            {
                if (IsCurrentDetail(itemId, version)) { _actionBusy = false; UpdateActionState(); }
            }
        }
        if (!IsCurrentDetail(itemId, version)) return;
        var local = _localTrailers.ToArray();
        var remote = RemoteTrailerUris().ToArray();
        if (local.Length == 1 && remote.Length == 0)
        {
            PlayRequested?.Invoke(this, new LumenPlayRequestEventArgs(local[0]));
            return;
        }
        if (local.Length == 0 && remote.Length == 1)
        {
            await OpenRemoteTrailerAsync(remote[0].Uri);
            return;
        }
        if (local.Length + remote.Length == 0)
        {
            SetNotice(LumenText.Get(_localTrailerFailed ? "Trailers could not be loaded." : "No trailer is available."), _localTrailerFailed);
            return;
        }
        if (_trailerButton is null) return;
        var list = new StackPanel { Spacing = 2 };
        var popup = new Flyout { FlyoutPresenterStyle = PopupStyle(236) };
        foreach (var trailer in local)
        {
            var choice = PopupAction(trailer.Name ?? LumenText.Get("Local trailer"), "clapperboard");
            choice.Click += (_, _) => { popup.Hide(); if (IsCurrentDetail(itemId, version)) PlayRequested?.Invoke(this, new LumenPlayRequestEventArgs(trailer)); };
            list.Children.Add(choice);
        }
        foreach (var trailer in remote)
        {
            var choice = PopupAction(trailer.Name, "globe");
            choice.Click += async (_, _) => { popup.Hide(); if (IsCurrentDetail(itemId, version)) await OpenRemoteTrailerAsync(trailer.Uri); };
            list.Children.Add(choice);
        }
        popup.Content = list;
        popup.ShowAt(_trailerButton);
    }

    private async Task OpenRemoteTrailerAsync(Uri uri)
    {
        var itemId = _detailId;
        var version = _detailVersion;
        void ShowError()
        {
            if (itemId is not null && IsCurrentDetail(itemId, version)) SetNotice(LumenText.Get("The trailer could not be opened."), true);
        }
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(uri)) ShowError();
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or ArgumentException)
        { ShowError(); }
    }

    private static bool ExpectedRequestFailure(Exception exception) => exception is EmbyApiException or EmbyTransportException
        or EmbyProtocolException or TimeoutException;
    private static string DescribeRequestFailure(Exception exception) => LumenText.Get(exception switch
    {
        EmbyApiException { IsAuthenticationFailure: true } => "Your sign-in was not accepted or has expired. Check your account and sign in again.",
        EmbyApiException { IsPermissionDenied: true } => "Your Emby account does not have permission to do this.",
        EmbyTransportException => "Could not reach the Emby server. Check the address and your connection.",
        TimeoutException => "The request timed out. Check your connection and try again.",
        _ => "The operation could not be completed. Try again."
    });
}
