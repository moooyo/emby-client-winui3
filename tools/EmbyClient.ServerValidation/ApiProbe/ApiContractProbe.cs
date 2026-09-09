using System.Diagnostics;
using System.Globalization;
using System.Net;
using EmbyClient.Api;
using EmbyClient.Playback;

namespace EmbyClient.ServerValidation.ApiProbe;

internal sealed class ApiContractProbe(Options options, Credentials credentials)
{
    private readonly ProbeReport _report = new() { ExpectedServerVersion = options.ExpectedVersion };
    private readonly Dictionary<string, bool> _expected = CreateExpectedSteps();
    private EmbyApiClient? _api;
    private string? _sessionId;
    private BaseItemDto? _item;
    private bool? _favoriteBaseline;
    private bool? _playedBaseline;
    private Negotiation? _original;
    private Negotiation? _hls;
    private Negotiation? _subtitle;
    private Negotiation? _reported;
    private bool _startAttempted;
    private bool _originalBytesRead;
    private bool _hlsBytesRead;

    public async Task<ProbeReport> RunAsync(CancellationToken cancellationToken)
    {
        using var transport = EmbyApiClient.CreateHttpClient();
        var identity = new ClientIdentity("EmbyClient API Validation", "Disposable API Probe",
            "api-probe-" + Guid.NewGuid().ToString("N"), "1.0.0");
        var publicApi = new EmbyApiClient(transport, new Uri(credentials.ServerUrl!), identity)
        {
            RequestTimeout = TimeSpan.FromSeconds(30)
        };
        try
        {
            if (!await CheckAsync("Server.PublicInfo", async () =>
            {
                var info = await publicApi.GetPublicSystemInfoAsync(cancellationToken);
                Require(Version.TryParse(info.Version, out _), "MissingServerVersion");
                _report.ObservedServerVersion = info.Version;
                Require(info.Version == options.ExpectedVersion, "UnexpectedServerVersion");
                Require(!string.IsNullOrWhiteSpace(info.Id), "MissingServerId");
                return Evidence(("Version", info.Version!), ("ServerIdPresent", "true"));
            })) throw new PrerequisiteException();

            if (!await CheckAsync("Authentication.ValidCredentials", async () =>
            {
                var result = await publicApi.AuthenticateByNameAsync(credentials.Username!, credentials.Password!, cancellationToken);
                Require(!string.IsNullOrWhiteSpace(result.AccessToken) && !string.IsNullOrWhiteSpace(result.User?.Id), "MissingAuthenticationContext");
                _api = publicApi.WithAuthentication(result.AccessToken!, result.User!.Id!);
                _sessionId = result.SessionInfo?.Id;
                Require(!string.IsNullOrWhiteSpace(_sessionId), "MissingSessionId");
                return Evidence(("TokenPresent", "true"), ("UserPresent", "true"), ("SessionPresent", "true"));
            })) throw new PrerequisiteException();

            await CheckAsync("Authentication.WrongPassword", async () =>
            {
                AuthenticationResult unexpected;
                try
                {
                    unexpected = await publicApi.AuthenticateByNameAsync(credentials.Username!,
                        "invalid-" + Guid.NewGuid().ToString("N") + "-" + Guid.NewGuid().ToString("N"), cancellationToken);
                }
                catch (EmbyApiException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return Evidence(("HttpStatus", Number((int)exception.StatusCode)), ("Rejected", "true"));
                }
                if (!string.IsNullOrWhiteSpace(unexpected.AccessToken) && !string.IsNullOrWhiteSpace(unexpected.User?.Id))
                {
                    await CheckAsync("Cleanup.UnexpectedAuthentication", async () =>
                    {
                        await publicApi.WithAuthentication(unexpected.AccessToken, unexpected.User.Id).LogoutAsync(CancellationToken.None);
                        return Evidence(("ServerAccepted", "true"));
                    });
                }
                throw new ProbeAssertionException("InvalidPasswordAccepted");
            });
            await CheckAsync("User.Current", async () =>
            {
                var user = await _api!.GetCurrentUserAsync(cancellationToken);
                Require(user.Id == _api.UserId, "CurrentUserMismatch");
                Require(user.Policy?.IsDisabled != true, "CurrentUserDisabled");
                return Evidence(("MatchesAuthenticatedUser", "true"), ("PolicyPresent", Bool(user.Policy is not null)));
            });
            await CheckAsync("Server.AuthenticatedInfo", async () =>
            {
                var info = await _api!.GetSystemInfoAsync(cancellationToken);
                Require(info.Version == options.ExpectedVersion, "AuthenticatedVersionMismatch");
                return Evidence(("VersionMatches", "true"));
            });
            await CheckAsync("Session.Capabilities", async () =>
            {
                await _api!.SetCapabilitiesAsync(_sessionId!, new ClientCapabilities
                {
                    PlayableMediaTypes = ["Video"],
                    SupportedCommands = [],
                    SupportsMediaControl = false,
                    SupportsSync = false,
                    DeviceProfile = ConservativeDeviceProfile.Create()
                }, cancellationToken);
                return Evidence(("ServerAccepted", "true"), ("Profile", "Application ConservativeDeviceProfile"));
            });
            await CheckAsync("Library.Views", async () =>
            {
                var views = await _api!.GetViewsAsync(cancellationToken: cancellationToken);
                Require(views.Items.Length > 0, "NoLibraryViews");
                return Evidence(("Count", Number(views.Items.Length)));
            });

            QueryResult<BaseItemDto>? items = null;
            await CheckAsync("Items.List", async () =>
            {
                items = await _api!.GetItemsAsync(VideoQuery() with { Limit = 100 }, cancellationToken);
                Require(items.Items.Length > 0, "NoVideoItems");
                var selected = options.ItemId is null ? items.Items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Id))
                    : items.Items.FirstOrDefault(item => item.Id == options.ItemId);
                if (selected is not null) _item = selected;
                return Evidence(("Count", Number(items.Items.Length)), ("TotalRecordCount", Number(items.TotalRecordCount)));
            });
            await CheckAsync("Items.Latest", async () =>
            {
                var latest = await _api!.GetLatestItemsAsync(new LatestItemsQuery { Limit = 10 }, cancellationToken);
                return Evidence(("Count", Number(latest.Length)));
            });
            await CheckAsync("Items.Resume", async () =>
            {
                var resume = await _api!.GetResumeItemsAsync(VideoQuery() with { Limit = 10 }, cancellationToken);
                return Evidence(("Count", Number(resume.Items.Length)));
            });
            await CheckAsync("Items.NextUp", async () =>
            {
                var next = await _api!.GetNextUpAsync(new NextUpQuery { Limit = 10 }, cancellationToken);
                return Evidence(("Count", Number(next.Items.Length)));
            });
            await CheckAsync("Items.EmptySearch", async () =>
            {
                var empty = await _api!.GetItemsAsync(VideoQuery() with { SearchTerm = "not-found-" + Guid.NewGuid().ToString("N") }, cancellationToken);
                Require(empty.Items.Length == 0 && empty.TotalRecordCount is null or 0, "ExpectedEmptySearch");
                return Evidence(("Count", "0"));
            });
            await CheckAsync("Items.Paging", async () =>
            {
                var first = await _api!.GetItemsAsync(VideoQuery() with { StartIndex = 0, Limit = 1 }, cancellationToken);
                var second = await _api.GetItemsAsync(VideoQuery() with { StartIndex = 1, Limit = 1 }, cancellationToken);
                Require(first.Items.Length == 1 && first.TotalRecordCount is >= 1, "InvalidFirstPage");
                if (first.TotalRecordCount == 1) Require(second.Items.Length == 0, "ExpectedEmptySecondPage");
                else Require(second.Items.Length == 1 && second.Items[0].Id != first.Items[0].Id, "DuplicateOrMissingSecondPage");
                var beyond = await _api.GetItemsAsync(VideoQuery() with { StartIndex = first.TotalRecordCount, Limit = 1 }, cancellationToken);
                Require(beyond.Items.Length == 0, "ExpectedEmptyPagePastEnd");
                return Evidence(("FirstPageCount", Number(first.Items.Length)), ("SecondPageCount", Number(second.Items.Length)), ("PastEndCount", "0"));
            });

            if (options.ItemId is null && _item?.Id is null) throw new PrerequisiteException();
            if (!await CheckAsync("Items.Details", async () =>
            {
                var id = options.ItemId ?? _item!.Id!;
                _item = await _api!.GetItemAsync(id, cancellationToken);
                Require(_item.Id == id && _item.RunTimeTicks > 0, "InvalidVideoDetails");
                Require(_item.UserData?.IsFavorite.HasValue == true && _item.UserData.Played.HasValue, "MissingReversibleUserData");
                _favoriteBaseline = _item.UserData!.IsFavorite;
                _playedBaseline = _item.UserData.Played;
                return Evidence(("DurationTicks", Number(_item.RunTimeTicks)),
                    ("MediaSourceCount", Number(_item.MediaSources?.Length ?? 0)), ("UserDataPresent", "true"));
            })) throw new PrerequisiteException();
            await CheckAsync("Items.Search", async () =>
            {
                Require(!string.IsNullOrWhiteSpace(_item!.Name), "MissingSearchableName");
                var search = await _api!.GetItemsAsync(VideoQuery() with { SearchTerm = _item.Name, Limit = 100 }, cancellationToken);
                Require(search.Items.Any(item => item.Id == _item.Id), "SearchDidNotReturnItem");
                return Evidence(("SelectedItemFound", "true"), ("Count", Number(search.Items.Length)));
            });

            await VerifyUserFlagsAsync(cancellationToken);
            await VerifyMediaAsync(transport, cancellationToken);
        }
        catch (PrerequisiteException)
        {
            // Missing dependencies are represented by explicit blocked steps after cleanup.
        }
        catch (Exception exception)
        {
            _report.Steps.Add(Failed("Probe.UnexpectedFailure", exception, 0));
        }
        finally
        {
            await CleanupAsync(transport);
        }
        return Finish();
    }

    private async Task VerifyUserFlagsAsync(CancellationToken cancellationToken)
    {
        await CheckAsync("UserData.FavoriteToggle", async () =>
        {
            var target = !_favoriteBaseline!.Value;
            var written = await _api!.SetFavoriteAsync(_item!.Id!, target, cancellationToken);
            var read = await _api.GetItemAsync(_item.Id!, cancellationToken);
            Require(written.IsFavorite == target && read.UserData?.IsFavorite == target, "FavoriteReadbackMismatch");
            return Evidence(("MutationAndReadbackMatch", "true"));
        });
        await CheckAsync("UserData.FavoriteRestore", async () =>
        {
            await _api!.SetFavoriteAsync(_item!.Id!, _favoriteBaseline!.Value, CancellationToken.None);
            var read = await _api.GetItemAsync(_item.Id!, CancellationToken.None);
            Require(read.UserData?.IsFavorite == _favoriteBaseline, "FavoriteRestoreMismatch");
            return Evidence(("BaselineRestored", "true"));
        });
        await CheckAsync("UserData.PlayedToggle", async () =>
        {
            var target = !_playedBaseline!.Value;
            var written = await _api!.SetPlayedAsync(_item!.Id!, target, cancellationToken);
            var read = await _api.GetItemAsync(_item.Id!, cancellationToken);
            Require(written.Played == target && read.UserData?.Played == target, "PlayedReadbackMismatch");
            return Evidence(("MutationAndReadbackMatch", "true"));
        });
        await CheckAsync("UserData.PlayedRestore", async () =>
        {
            await _api!.SetPlayedAsync(_item!.Id!, _playedBaseline!.Value, CancellationToken.None);
            var read = await _api.GetItemAsync(_item.Id!, CancellationToken.None);
            Require(read.UserData?.Played == _playedBaseline, "PlayedRestoreMismatch");
            return Evidence(("BaselineBooleanRestored", "true"));
        });
    }

    private async Task VerifyMediaAsync(HttpClient transport, CancellationToken cancellationToken)
    {
        var media = new MediaTransfer(transport, _api!);
        await CheckAsync("PlaybackInfo.Original", async () =>
        {
            var response = await _api!.GetPlaybackInfoAsync(_item!.Id!, CreatePlaybackRequest(false), cancellationToken);
            _original = CaptureNegotiation(response);
            ValidateNegotiation(response, _original);
            Require(_original.Source?.SupportsDirectStream == true, "OriginalStreamNotNegotiated");
            return NegotiationEvidence(_original.Source!);
        });
        if (_original?.Source is { } originalSource && _original.PlaySessionId is not null)
        {
            _originalBytesRead = await CheckAsync("Media.OriginalRange", async () =>
            {
                var uri = _api!.BuildVideoStreamUri(_item!.Id!, originalSource.Id!, _original.PlaySessionId);
                return await media.ReadRangeAsync(uri, originalSource, cancellationToken);
            });
        }
        await CheckAsync("PlaybackInfo.ForcedHls", async () =>
        {
            var response = await _api!.GetPlaybackInfoAsync(_item!.Id!, CreatePlaybackRequest(true), cancellationToken);
            _hls = CaptureNegotiation(response);
            ValidateNegotiation(response, _hls);
            Require(_hls.Source?.SupportsTranscoding == true && !string.IsNullOrWhiteSpace(_hls.Source.TranscodingUrl), "HlsNotNegotiated");
            Require(string.Equals(_hls.Source!.TranscodingSubProtocol, "hls", StringComparison.OrdinalIgnoreCase), "ExpectedHlsSubProtocol");
            return NegotiationEvidence(_hls.Source);
        });
        if (_hls?.Source is { TranscodingUrl: not null } hlsSource)
        {
            PlaylistResult? playlist = null;
            await CheckAsync("Media.HlsManifest", async () =>
            {
                playlist = await media.ReadHlsAsync(_api!.ResolveMediaUri(hlsSource.TranscodingUrl), hlsSource, cancellationToken);
                return playlist.Evidence;
            });
            if (playlist is not null)
                _hlsBytesRead = await CheckAsync("Media.HlsSegment", () => media.ReadSegmentAsync(playlist.SegmentUri, hlsSource, cancellationToken));
        }

        var subtitleStream = _original?.Source?.MediaStreams.FirstOrDefault(stream => stream.Type == "Subtitle"
            && (stream.IsTextSubtitleStream == true || stream.Codec is "srt" or "subrip" or "vtt" or "webvtt"));
        if (subtitleStream is not null)
        {
            _expected["Subtitles.ExternalNegotiation"] = true;
            _expected["Media.WebVttSubtitle"] = true;
            _expected["Cleanup.SubtitleEncoding"] = true;
            MediaStream? selectedSubtitle = null;
            await CheckAsync("Subtitles.ExternalNegotiation", async () =>
            {
                var request = CreatePlaybackRequest(false) with
                {
                    SubtitleStreamIndex = subtitleStream.Index,
                    DeviceProfile = ConservativeDeviceProfile.Create(enableExternalWebVtt: true)
                };
                var response = await _api!.GetPlaybackInfoAsync(_item!.Id!, request, cancellationToken);
                _subtitle = CaptureNegotiation(response);
                ValidateNegotiation(response, _subtitle);
                selectedSubtitle = _subtitle.Source!.MediaStreams.FirstOrDefault(stream => stream.Index == subtitleStream.Index && stream.Type == "Subtitle");
                Require(selectedSubtitle is not null, "SelectedSubtitleStreamMissing");
                Require(string.Equals(selectedSubtitle!.DeliveryMethod, "External", StringComparison.OrdinalIgnoreCase), "ExternalSubtitleNotNegotiated");
                return Evidence(("SubtitleStreamIndex", Number(selectedSubtitle.Index)), ("DeliveryMethod", "External"),
                    ("HasDeliveryUrl", Bool(!string.IsNullOrWhiteSpace(selectedSubtitle.DeliveryUrl))),
                    ("Profile", "Application ConservativeDeviceProfile with external WebVTT"));
            });
            if (_subtitle?.Source is { } subtitleSource && selectedSubtitle is not null)
            {
                await CheckAsync("Media.WebVttSubtitle", async () =>
                {
                    var uri = !string.IsNullOrWhiteSpace(selectedSubtitle.DeliveryUrl)
                        ? _api!.ResolveMediaUri(selectedSubtitle.DeliveryUrl)
                        : new Uri(_api!.ApiRoot, "Videos/" + Uri.EscapeDataString(_item!.Id!) + "/"
                            + Uri.EscapeDataString(subtitleSource.Id!) + "/Subtitles/" + Number(selectedSubtitle.Index) + "/Stream.vtt");
                    return await media.ReadSubtitleAsync(uri, subtitleSource, selectedSubtitle.Index, cancellationToken);
                });
            }
        }

        _reported = _hlsBytesRead ? _hls : _originalBytesRead ? _original : null;
        if (_reported?.Source is null) return;
        if (!await CheckAsync("PlaybackReport.Start", async () =>
        {
            _startAttempted = true;
            await _api!.ReportPlaybackStartAsync(new PlaybackStartInfo
            {
                ItemId = _item!.Id, MediaSourceId = _reported.Source.Id, PlaySessionId = _reported.PlaySessionId,
                SessionId = _sessionId, PositionTicks = 0, RunTimeTicks = _item.RunTimeTicks,
                CanSeek = true, IsPaused = true, IsMuted = true, VolumeLevel = 0,
                SubtitleStreamIndex = -1, PlayMethod = _reported == _hls ? "Transcode" : "DirectStream",
                EventName = "TimeUpdate", PlaybackRate = 1
            }, cancellationToken);
            return Evidence(("ServerAccepted", "true"), ("ReportSource", "Synthetic API probe; no decoder"));
        })) return;
        await CheckAsync("Session.StartReadback", async () =>
        {
            var read = await media.ReadSessionAsync(_sessionId!, cancellationToken);
            Require(read.Found && read.ItemId == _item!.Id, "SessionStartReadbackMismatch");
            return Evidence(("NowPlayingItemMatches", "true"), ("Transport", "Supplemental authenticated Sessions GET"));
        });
        await CheckAsync("PlaybackReport.Progress", async () =>
        {
            await _api!.ReportPlaybackProgressAsync(new PlaybackProgressInfo
            {
                ItemId = _item!.Id, MediaSourceId = _reported.Source.Id, PlaySessionId = _reported.PlaySessionId,
                SessionId = _sessionId, PositionTicks = TimeSpan.TicksPerSecond,
                CanSeek = true, IsPaused = true, IsMuted = true, VolumeLevel = 0,
                PlayMethod = _reported == _hls ? "Transcode" : "DirectStream", EventName = "TimeUpdate", PlaybackRate = 1
            }, cancellationToken);
            return Evidence(("ServerAccepted", "true"), ("SyntheticPositionTicks", Number(TimeSpan.TicksPerSecond)));
        });
        await CheckAsync("Session.ProgressReadback", async () =>
        {
            var read = await media.ReadSessionAsync(_sessionId!, cancellationToken);
            Require(read.Found && read.ItemId == _item!.Id && read.PositionTicks == TimeSpan.TicksPerSecond, "SessionProgressReadbackMismatch");
            return Evidence(("PositionMatchesSyntheticReport", "true"), ("Transport", "Supplemental authenticated Sessions GET"));
        });
    }

    private async Task CleanupAsync(HttpClient transport)
    {
        if (_api is null) return;
        if (_startAttempted && _reported is not null)
        {
            await CheckAsync("PlaybackReport.Stop", async () =>
            {
                await _api.ReportPlaybackStoppedAsync(new PlaybackStopInfo
                {
                    ItemId = _item!.Id, MediaSourceId = _reported.Source?.Id, PlaySessionId = _reported.PlaySessionId,
                    SessionId = _sessionId, PositionTicks = _item.UserData?.PlaybackPositionTicks ?? 0,
                    Failed = false
                }, CancellationToken.None);
                return Evidence(("ServerAccepted", "true"), ("ReportSource", "Synthetic API probe; no decoder"));
            });
            await CheckAsync("Session.StopReadback", async () =>
            {
                var read = await new MediaTransfer(transport, _api).ReadSessionAsync(_sessionId!, CancellationToken.None);
                Require(read.Found && read.ItemId is null, "SessionMissingOrStillPlayingAfterStop");
                return Evidence(("NowPlayingItemCleared", "true"), ("Transport", "Supplemental authenticated Sessions GET"));
            });
        }
        foreach (var pair in new[] { ("Cleanup.OriginalEncoding", _original), ("Cleanup.HlsEncoding", _hls), ("Cleanup.SubtitleEncoding", _subtitle) })
        {
            if (string.IsNullOrWhiteSpace(pair.Item2?.PlaySessionId)) continue;
            await CheckAsync(pair.Item1, async () =>
            {
                await _api.StopActiveEncodingsAsync(pair.Item2.PlaySessionId, CancellationToken.None);
                return Evidence(("ServerAccepted", "true"));
            });
            if (pair.Item2.Source?.RequiresClosing == true && !string.IsNullOrWhiteSpace(pair.Item2.Source.LiveStreamId))
            {
                await CheckAsync(pair.Item1 + ".LiveStreamClose", async () =>
                {
                    await _api.CloseLiveStreamAsync(pair.Item2.Source.LiveStreamId, CancellationToken.None);
                    return Evidence(("ServerAccepted", "true"));
                });
            }
        }
        if (_item?.Id is not null && _favoriteBaseline.HasValue && _playedBaseline.HasValue)
        {
            await CheckAsync("Cleanup.FavoriteRestore", async () =>
            {
                await _api.SetFavoriteAsync(_item.Id, _favoriteBaseline.Value, CancellationToken.None);
                return Evidence(("ServerAccepted", "true"));
            });
            await CheckAsync("Cleanup.PlayedRestore", async () =>
            {
                await _api.SetPlayedAsync(_item.Id, _playedBaseline.Value, CancellationToken.None);
                return Evidence(("ServerAccepted", "true"));
            });
            await CheckAsync("Cleanup.UserDataBooleans", async () =>
            {
                var read = await _api.GetItemAsync(_item.Id, CancellationToken.None);
                Require(read.UserData?.IsFavorite == _favoriteBaseline && read.UserData?.Played == _playedBaseline, "FinalUserDataRestoreMismatch");
                return Evidence(("FavoriteBaselineRestored", "true"), ("PlayedBaselineRestored", "true"),
                    ("OriginalPositionTicks", Number(_item.UserData?.PlaybackPositionTicks)),
                    ("FinalPositionTicks", Number(read.UserData?.PlaybackPositionTicks)),
                    ("HistoricalCountersRestored", "NotSupportedByPublicClient"));
            });
        }
        await CheckAsync("Session.Logout", async () =>
        {
            await _api.LogoutAsync(CancellationToken.None);
            return Evidence(("ServerAccepted", "true"));
        });
        await CheckAsync("Authentication.LoggedOutTokenRejected", async () =>
        {
            try
            {
                await _api.GetCurrentUserAsync(CancellationToken.None);
                throw new ProbeAssertionException("LoggedOutTokenStillAccepted");
            }
            catch (EmbyApiException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Evidence(("HttpStatus", Number((int)exception.StatusCode)), ("Rejected", "true"));
            }
        });
    }

    private static PlaybackInfoRequest CreatePlaybackRequest(bool forceTranscoding) => new()
    {
        MaxStreamingBitrate = 20_000_000, StartTimeTicks = 0, SubtitleStreamIndex = -1, MaxAudioChannels = 2,
        DeviceProfile = ConservativeDeviceProfile.Create(), EnableDirectPlay = false,
        EnableDirectStream = !forceTranscoding, EnableTranscoding = true,
        AllowVideoStreamCopy = !forceTranscoding, AllowAudioStreamCopy = !forceTranscoding,
        AllowInterlacedVideoStreamCopy = false, IsPlayback = true, AutoOpenLiveStream = false
    };

    private static Negotiation CaptureNegotiation(PlaybackInfoResponse response) => new(response.PlaySessionId,
        response.MediaSources?.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.Id)));

    private static void ValidateNegotiation(PlaybackInfoResponse response, Negotiation negotiation)
    {
        Require(string.IsNullOrWhiteSpace(response.ErrorCode), "PlaybackNegotiationRejected");
        Require(!string.IsNullOrWhiteSpace(negotiation.PlaySessionId), "MissingPlaySessionId");
        Require(negotiation.Source is not null, "MissingMediaSource");
        Require(negotiation.Source!.RequiresOpening != true, "LiveStreamFixtureNotSupported");
    }

    private static Dictionary<string, string> NegotiationEvidence(MediaSourceInfo source) => Evidence(
        ("MediaSourceIdPresent", Bool(!string.IsNullOrWhiteSpace(source.Id))),
        ("SupportsDirectStream", Bool(source.SupportsDirectStream)), ("SupportsTranscoding", Bool(source.SupportsTranscoding)),
        ("HasDirectStreamUrl", Bool(!string.IsNullOrWhiteSpace(source.DirectStreamUrl))),
        ("HasTranscodingUrl", Bool(!string.IsNullOrWhiteSpace(source.TranscodingUrl))),
        ("HlsSubProtocol", Bool(string.Equals(source.TranscodingSubProtocol, "hls", StringComparison.OrdinalIgnoreCase))),
        ("AudioStreamCount", Number(source.MediaStreams.Count(stream => stream.Type == "Audio"))),
        ("VideoStreamCount", Number(source.MediaStreams.Count(stream => stream.Type == "Video"))),
        ("SubtitleStreamCount", Number(source.MediaStreams.Count(stream => stream.Type == "Subtitle"))));

    private static ItemQuery VideoQuery() => new()
    {
        Recursive = true, MediaTypes = ["Video"], SortBy = ["SortName"], SortOrder = ["Ascending"],
        Fields = ["MediaSources", "MediaStreams"], EnableUserData = true, Limit = 50
    };

    private async Task<bool> CheckAsync(string name, Func<Task<Dictionary<string, string>>> check)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var evidence = await check();
            _report.Steps.Add(new ProbeStep { Name = name, Status = "Passed", Required = _expected.GetValueOrDefault(name, true), DurationMilliseconds = timer.ElapsedMilliseconds, Evidence = evidence });
            Console.WriteLine("PASS " + name);
            return true;
        }
        catch (Exception exception)
        {
            _report.Steps.Add(Failed(name, exception, timer.ElapsedMilliseconds));
            Console.WriteLine("FAIL " + name);
            return false;
        }
    }

    private ProbeStep Failed(string name, Exception exception, long milliseconds) => new()
    {
        Name = name, Status = "Failed", Required = _expected.GetValueOrDefault(name, true), DurationMilliseconds = milliseconds,
        ErrorCode = exception switch
        {
            ProbeAssertionException assertion => assertion.Code,
            EmbyApiException => "EmbyHttpError",
            ProbeHttpException => "MediaHttpError",
            EmbyProtocolException => "EmbyResponseContractMismatch",
            EmbyTransportException => "EmbyTransportFailure",
            System.Text.Json.JsonException => "SupplementalResponseContractMismatch",
            HttpRequestException => "HttpTransportFailure",
            OperationCanceledException => "CancelledOrTimedOut",
            TimeoutException => "RequestTimedOut",
            _ => "UnexpectedFailure"
        },
        HttpStatus = exception switch { EmbyApiException api => (int)api.StatusCode, ProbeHttpException http => http.Status, _ => null }
    };

    private ProbeReport Finish()
    {
        foreach (var planned in _expected)
        {
            if (_report.Steps.Any(step => step.Name == planned.Key)) continue;
            _report.Steps.Add(new ProbeStep { Name = planned.Key, Status = "Blocked", Required = planned.Value, ErrorCode = "PrerequisiteNotCompleted" });
        }
        _report.FinishedUtc = DateTimeOffset.UtcNow;
        _report.Outcome = _report.Steps.Any(step => step.Required && step.Status is "Failed" or "Blocked") ? "Failed" : "Passed";
        return _report;
    }

    private static Dictionary<string, bool> CreateExpectedSteps()
    {
        var required = new[]
        {
            "Server.PublicInfo", "Authentication.ValidCredentials", "Authentication.WrongPassword", "User.Current",
            "Session.Capabilities", "Library.Views", "Items.List", "Items.Latest", "Items.Resume", "Items.NextUp",
            "Items.Details", "Items.Search", "Items.EmptySearch", "Items.Paging", "UserData.FavoriteToggle",
            "UserData.FavoriteRestore", "UserData.PlayedToggle", "UserData.PlayedRestore", "PlaybackInfo.Original",
            "Media.OriginalRange", "PlaybackInfo.ForcedHls", "Media.HlsManifest", "Media.HlsSegment", "PlaybackReport.Start",
            "PlaybackReport.Progress", "PlaybackReport.Stop", "Cleanup.OriginalEncoding", "Cleanup.HlsEncoding",
            "Cleanup.FavoriteRestore", "Cleanup.PlayedRestore", "Cleanup.UserDataBooleans", "Session.Logout", "Authentication.LoggedOutTokenRejected"
        }.ToDictionary(name => name, _ => true, StringComparer.Ordinal);
        foreach (var name in new[] { "Server.AuthenticatedInfo", "Session.StartReadback", "Session.ProgressReadback", "Session.StopReadback" })
            required.Add(name, false);
        return required;
    }

    private static Dictionary<string, string> Evidence(params (string Key, string Value)[] pairs) => pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";
    private static string Bool(bool? value) => value?.ToString().ToLowerInvariant() ?? "null";
    private static void Require(bool condition, string code)
    {
        if (!condition) throw new ProbeAssertionException(code);
    }

    private sealed record Negotiation(string? PlaySessionId, MediaSourceInfo? Source);
    private sealed class PrerequisiteException : Exception;
}
