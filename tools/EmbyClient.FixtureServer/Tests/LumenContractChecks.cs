using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LumenContractChecks
{
    private const string UserId = "synthetic-user-demo";
    private const string UserPath = "/emby/Users/" + UserId;

    public static async Task RunAsync(string serverPath, string testRoot, byte[] bytes, Action<bool, string> check)
    {
        using var metadataDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(testRoot, "fixture-h264-aac.json")));
        var metadata = metadataDocument.RootElement;
        var duration = metadata.GetProperty("DurationTicks").GetInt64();
        var artworkDirectory = Path.GetFullPath(Path.Combine(testRoot, "lumen-handoff"));
        if (!string.Equals(Path.GetDirectoryName(artworkDirectory), Path.GetFullPath(testRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The owned artwork directory must remain directly inside the owned test directory.");
        var artwork = await CreateArtworkAsync(artworkDirectory);
        await using var server = await TestServer.StartAsync(serverPath, testRoot,
            "--lumen-catalog", "--artwork-directory", artworkDirectory);
        using var anonymous = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = server.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(10)
        };
        var requestCount = 0;

        var publicInfo = await JsonAsync("/emby/System/Info/Public");
        var views = await JsonAsync(UserPath + "/Views");
        check(publicInfo.GetProperty("Id").GetString() == "synthetic-lumen-server-0001"
            && Ids(views).SequenceEqual(["movies", "shows", "collections"]),
            "Lumen uses a distinct synthetic server identity and three library views.");

        const string movieQuery = UserPath + "/Items?ParentId=movies&IncludeItemTypes=Movie&SortBy=ProductionYear,SortName&SortOrder=Descending,Ascending";
        var firstPage = await JsonAsync(movieQuery + "&Limit=7");
        var secondPage = await JsonAsync(movieQuery + "&StartIndex=7&Limit=7");
        var movies = Items(await JsonAsync(movieQuery + "&Limit=50"));
        check(movies.Length == 40 && firstPage.GetProperty("TotalRecordCount").GetInt32() == 40
            && secondPage.GetProperty("TotalRecordCount").GetInt32() == 40
            && Ids(firstPage).Concat(Ids(secondPage)).SequenceEqual(movies.Take(14).Select(Id)),
            "Movie pages preserve a stable total, order, and non-overlapping offsets.");
        check(movies.SequenceEqual(movies.OrderByDescending(item => item.GetProperty("ProductionYear").GetInt32())
                .ThenBy(item => item.GetProperty("SortName").GetString(), StringComparer.OrdinalIgnoreCase)),
            "Production-year sorting honors its secondary title direction.");
        var generic = await JsonAsync(UserPath + "/Items?Limit=500");
        check(Items(generic).All(item => Type(item) is not ("Genre" or "Person" or "Trailer")),
            "The normal media query does not leak facet or trailer entries.");

        var genrePage = await JsonAsync("/emby/Genres?UserId=" + UserId + "&ParentId=movies&IncludeItemTypes=Movie&Limit=3");
        var nextGenrePage = await JsonAsync("/emby/Genres?UserId=" + UserId + "&ParentId=movies&IncludeItemTypes=Movie&StartIndex=3&Limit=3");
        var genres = Items(await JsonAsync("/emby/Genres?UserId=" + UserId + "&ParentId=movies&IncludeItemTypes=Movie&Limit=50"));
        check(genres.Length == 16 && genrePage.GetProperty("TotalRecordCount").GetInt32() == 16
            && Ids(genrePage).Concat(Ids(nextGenrePage)).SequenceEqual(genres.Take(6).Select(Id))
            && genres.All(item => Type(item) == "Genre"), "Genre facets have paged item-array contracts.");
        check(movies.SelectMany(item => item.GetProperty("GenreItems").EnumerateArray()).All(pair =>
            genres.Any(genre => Id(genre) == pair.GetProperty("Id").GetInt64().ToString(CultureInfo.InvariantCulture)
                && genre.GetProperty("Name").GetString() == pair.GetProperty("Name").GetString())),
            "Every advertised numeric genre pair resolves to the matching genre node.");
        var scopedGenres = await JsonAsync("/emby/Genres?UserId=" + UserId + "&ParentId=collection-night&Limit=50");
        string[] nightIds = ["1002", "1015", "1029"];
        check(Items(scopedGenres).Select(item => item.GetProperty("Name").GetString()!).ToHashSet(StringComparer.Ordinal)
                .SetEquals(movies.Where(item => nightIds.Contains(Id(item), StringComparer.Ordinal)).SelectMany(GenreNames)),
            "Collection genre facets are scoped to their members.");

        var personPage = await JsonAsync("/emby/Persons?UserId=" + UserId + "&ParentId=movies&Limit=4");
        var nextPersonPage = await JsonAsync("/emby/Persons?UserId=" + UserId + "&ParentId=movies&StartIndex=4&Limit=4");
        var people = Items(await JsonAsync("/emby/Persons?UserId=" + UserId + "&ParentId=movies&Limit=50"));
        check(people.Length == 12 && personPage.GetProperty("TotalRecordCount").GetInt32() == 12
            && Ids(personPage).Concat(Ids(nextPersonPage)).SequenceEqual(people.Take(8).Select(Id))
            && people.All(item => Type(item) == "Person"), "Person facets preserve pagination and their DTO type.");
        var genreFiltered = await JsonAsync(UserPath + "/Items?ParentId=movies&IncludeItemTypes=Movie&Genres="
            + Uri.EscapeDataString("Science Fiction|Mystery") + "&Limit=50");
        check(Ids(genreFiltered).ToHashSet(StringComparer.Ordinal).SetEquals(movies.Where(item =>
            GenreNames(item).Any(genre => genre is "Science Fiction" or "Mystery")).Select(Id)),
            "Pipe-separated genre filters select the union of matching films.");
        var personFiltered = await JsonAsync(UserPath + "/Items?ParentId=movies&IncludeItemTypes=Movie&PersonIds=person-01&Limit=50");
        check(Ids(personFiltered).ToHashSet(StringComparer.Ordinal).SetEquals(movies.Where(item =>
            item.GetProperty("People").EnumerateArray().Any(person => Id(person) == "person-01")).Select(Id)),
            "Person filters select only works containing that person ID.");
        var tagFiltered = await JsonAsync(UserPath + "/Items?ParentId=movies&Tags=Night&Limit=50");
        check(Ids(tagFiltered).ToHashSet(StringComparer.Ordinal).SetEquals(movies.Where(item =>
            item.GetProperty("Tags").EnumerateArray().Any(tag => tag.GetString() == "Night")).Select(Id)),
            "Tag filters do not return unrelated films.");
        var favorites = await JsonAsync(UserPath + "/Items?ParentId=movies&Filters=IsFavorite&Limit=50");
        var unplayed = await JsonAsync(UserPath + "/Items?ParentId=movies&Filters=IsUnplayed&Limit=50");
        check(Ids(favorites).ToHashSet(StringComparer.Ordinal).SetEquals(movies.Where(item =>
                item.GetProperty("UserData").GetProperty("IsFavorite").GetBoolean()).Select(Id))
            && Ids(unplayed).ToHashSet(StringComparer.Ordinal).SetEquals(movies.Where(item =>
                !item.GetProperty("UserData").GetProperty("Played").GetBoolean()).Select(Id)),
            "Favorite and unplayed filters honor seeded user state.");
        var names = await JsonAsync(UserPath + "/Items?ParentId=movies&NameStartsWith=The&Limit=50");
        check(Ids(names).ToHashSet(StringComparer.Ordinal).SetEquals(movies.Where(item =>
            item.GetProperty("SortName").GetString()!.StartsWith("The", StringComparison.OrdinalIgnoreCase)).Select(Id)),
            "Alphabetical name filters preserve the selected library scope.");

        var seasons = await JsonAsync("/emby/Shows/2000/Seasons?UserId=" + UserId);
        var firstSeason = Items(await JsonAsync("/emby/Shows/2000/Episodes?UserId=" + UserId + "&SeasonId=2100"));
        var secondSeason = Items(await JsonAsync("/emby/Shows/2000/Episodes?UserId=" + UserId + "&SeasonId=2110"));
        var allEpisodes = Items(await JsonAsync("/emby/Shows/2000/Episodes?UserId=" + UserId));
        check(Ids(seasons).SequenceEqual(["2100", "2110"])
            && firstSeason.Length == 6 && secondSeason.Length == 6
            && ValidSeason(firstSeason, "2100", 1) && ValidSeason(secondSeason, "2110", 2)
            && allEpisodes.Select(Id).SequenceEqual(firstSeason.Concat(secondSeason).Select(Id)),
            "Two seasons expose six owned episodes each in season-then-episode order.");
        await StatusAsync(HttpMethod.Get, "/emby/Shows/2000/Episodes?UserId=" + UserId + "&SeasonId=2210",
            HttpStatusCode.NotFound, "An episode request cannot borrow another series' season.");
        var nextUp = await JsonAsync("/emby/Shows/NextUp?UserId=" + UserId + "&SeriesId=3000");
        check(Ids(nextUp).SequenceEqual(["3011"]), "Next-up starts with the first unplayed first-season episode.");
        await StatusAsync(HttpMethod.Post, UserPath + "/PlayedItems/3011", HttpStatusCode.OK,
            "Marking the current next-up episode played succeeds.");
        check(Ids(await JsonAsync("/emby/Shows/NextUp?UserId=" + UserId + "&SeriesId=3000")).SequenceEqual(["3012"]),
            "Next-up selects first-season episode two before second-season episode one.");
        await StatusAsync(HttpMethod.Delete, UserPath + "/PlayedItems/3011", HttpStatusCode.OK,
            "The next-up setup can be restored to unplayed.");
        var groupedLatest = await JsonAsync(UserPath + "/Items/Latest?ParentId=shows&IncludeItemTypes=Series&Limit=6");
        var episodeLatest = await JsonAsync(UserPath + "/Items/Latest?ParentId=shows&IncludeItemTypes=Episode&GroupItems=false&Limit=5");
        check(groupedLatest.ValueKind == JsonValueKind.Array && groupedLatest.GetArrayLength() == 6
            && groupedLatest.EnumerateArray().All(item => Type(item) == "Series")
            && groupedLatest.EnumerateArray().Select(Id).Distinct(StringComparer.Ordinal).Count() == 6
            && episodeLatest.ValueKind == JsonValueKind.Array && episodeLatest.GetArrayLength() == 5
            && episodeLatest.EnumerateArray().All(item => Type(item) == "Episode"),
            "Latest uses bare arrays and groups Series requests before applying their limit.");

        var film = await JsonAsync(UserPath + "/Items/1001");
        var episode = secondSeason.Single(item => Id(item) == "2115");
        var similar = await JsonAsync("/emby/Items/1001/Similar?UserId=" + UserId + "&Limit=4");
        check(Items(similar).Length == 4 && similar.GetProperty("TotalRecordCount").GetInt32() >= 4
            && Items(similar).All(item => Id(item) != "1001" && Type(item) == "Movie"
                && GenreNames(item).Intersect(GenreNames(film), StringComparer.OrdinalIgnoreCase).Any()),
            "Similar items exclude the source and preserve relevant film metadata.");
        var trailers = await JsonAsync(UserPath + "/Items/1001/LocalTrailers");
        check(trailers.ValueKind == JsonValueKind.Array && trailers.GetArrayLength() == 1
            && film.GetProperty("LocalTrailerCount").GetInt32() == trailers.GetArrayLength()
            && Id(trailers[0]) == "trailer-1001" && Type(trailers[0]) == "Trailer",
            "The advertised local trailer count agrees with its bare-array route.");
        var trailer = await JsonAsync(UserPath + "/Items/trailer-1001");
        foreach (var item in new[] { film, episode, trailer })
        {
            var sources = item.GetProperty("MediaSources").EnumerateArray().ToArray();
            var chapters = item.GetProperty("Chapters").EnumerateArray().ToArray();
            check(item.GetProperty("RunTimeTicks").GetInt64() == duration && sources.All(MeasuredSource)
                && item.GetProperty("MediaStreams").GetRawText() == sources[0].GetProperty("MediaStreams").GetRawText(),
                "Film, episode, and trailer details use the same measured synthetic media: " + Id(item));
            check(chapters.Length == 6 && chapters[0].GetProperty("StartPositionTicks").GetInt64() == 0
                && chapters.Select(chapter => chapter.GetProperty("StartPositionTicks").GetInt64()).SequenceEqual(
                    chapters.Select(chapter => chapter.GetProperty("StartPositionTicks").GetInt64()).Order())
                && chapters.All(chapter => chapter.GetProperty("StartPositionTicks").GetInt64() < duration)
                && chapters.Select(chapter => chapter.GetProperty("ChapterIndex").GetInt32()).SequenceEqual(Enumerable.Range(0, 6))
                && chapters.Any(chapter => chapter.GetProperty("MarkerType").GetString() == "IntroStart")
                && chapters.Any(chapter => chapter.GetProperty("MarkerType").GetString() == "IntroEnd"),
                "Chapter indices and markers stay inside the synthetic clip: " + Id(item));
        }
        check(film.GetProperty("MediaSources").GetArrayLength() == 2
            && film.GetProperty("MediaSources").EnumerateArray().Select(source => source.GetProperty("Id").GetString()).Distinct().Count() == 2,
            "The second film source is a distinct alias, not a different fabricated format.");

        const string playbackBody = """{"UserId":"synthetic-user-demo","IsPlayback":true}""";
        var playback = await JsonAsync("/emby/Items/1001/PlaybackInfo", HttpMethod.Post, playbackBody);
        var alternate = await JsonAsync("/emby/Items/1001/PlaybackInfo", HttpMethod.Post,
            """{"UserId":"synthetic-user-demo","IsPlayback":true,"MediaSourceId":"synthetic-mp4-alternate"}""");
        var trailerPlayback = await JsonAsync("/emby/Items/trailer-1001/PlaybackInfo", HttpMethod.Post, playbackBody);
        var episodePlayback = await JsonAsync("/emby/Items/2115/PlaybackInfo", HttpMethod.Post, playbackBody);
        check(playback.GetProperty("MediaSources").GetArrayLength() == 2
            && alternate.GetProperty("MediaSources").GetArrayLength() == 1
            && alternate.GetProperty("MediaSources")[0].GetProperty("Id").GetString() == "synthetic-mp4-alternate"
            && new[] { playback, alternate, trailerPlayback, episodePlayback }
                .SelectMany(value => value.GetProperty("MediaSources").EnumerateArray()).All(MeasuredSource),
            "Negotiated film aliases, episodes, and trailers retain the measured original-file contract.");
        foreach (var id in new[] { "person-01", "50001", "collection-night" })
            await StatusAsync(HttpMethod.Post, "/emby/Items/" + id + "/PlaybackInfo", HttpStatusCode.NotFound,
                "Non-media entries cannot negotiate playback: " + id, playbackBody);
        await RangeAsync(playback.GetProperty("MediaSources")[0].GetProperty("DirectStreamUrl").GetString()!, 100, 199);
        await RangeAsync(alternate.GetProperty("MediaSources")[0].GetProperty("DirectStreamUrl").GetString()!, 200, 299);
        await RangeAsync(trailerPlayback.GetProperty("MediaSources")[0].GetProperty("DirectStreamUrl").GetString()!, 100, 199);
        using (var range = await SendAsync(HttpMethod.Get,
            playback.GetProperty("MediaSources")[0].GetProperty("DirectStreamUrl").GetString()!, range: new RangeHeaderValue(bytes.Length, null)))
            check(range.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
                && range.Content.Headers.ContentRange?.Length == bytes.Length,
                "An unsatisfiable range advertises the real synthetic file length.");
        await StatusAsync(HttpMethod.Get, "/emby/Videos/1001/stream?PlaySessionId="
            + alternate.GetProperty("PlaySessionId").GetString() + "&MediaSourceId=synthetic-mp4", HttpStatusCode.NotFound,
            "A source-restricted playback session cannot stream the other alias.");

        var resume = await JsonAsync(UserPath + "/Items/Resume?Limit=50");
        var position = episode.GetProperty("UserData").GetProperty("PlaybackPositionTicks").GetInt64();
        check(Ids(resume).Contains("2115", StringComparer.Ordinal) && position > 0,
            "The seeded episode appears in continue watching with a nonzero position.");
        var hidden = await JsonAsync(UserPath + "/Items/2115/HideFromResume?Hide=true", HttpMethod.Post);
        check(hidden.GetProperty("PlaybackPositionTicks").GetInt64() == position
            && !Ids(await JsonAsync(UserPath + "/Items/Resume?Limit=50")).Contains("2115", StringComparer.Ordinal),
            "Hiding resume removes the episode without clearing its playback position.");
        var restored = await JsonAsync(UserPath + "/Items/2115/HideFromResume?Hide=false", HttpMethod.Post);
        check(restored.GetProperty("PlaybackPositionTicks").GetInt64() == position
            && Items(await JsonAsync(UserPath + "/Items/Resume?Limit=50")).Single(item => Id(item) == "2115")
                .GetProperty("UserData").GetProperty("PlaybackPositionTicks").GetInt64() == position,
            "Restoring resume preserves the original position and visibility.");
        await StatusAsync(HttpMethod.Post, UserPath + "/Items/2115/HideFromResume?Hide=invalid", HttpStatusCode.BadRequest,
            "Hide-from-resume requires a valid explicit boolean.");

        var originalConfiguration = (await JsonAsync(UserPath)).GetProperty("Configuration");
        const string configurationBody = """
            {"AudioLanguagePreference":"fra","SubtitleLanguagePreference":"eng","SubtitleMode":"Default",
             "EnableNextEpisodeAutoPlay":false,"RememberAudioSelections":true,"RememberSubtitleSelections":true,
             "ResumeRewindSeconds":7,"IntroSkipMode":"AutoSkip"}
            """;
        await StatusAsync(HttpMethod.Post, UserPath + "/Configuration", HttpStatusCode.NoContent,
            "The synthetic user accepts a supported configuration update.", configurationBody);
        var configuration = (await JsonAsync(UserPath)).GetProperty("Configuration");
        check(configuration.GetProperty("AudioLanguagePreference").GetString() == "fra"
            && configuration.GetProperty("SubtitleLanguagePreference").GetString() == "eng"
            && !configuration.GetProperty("EnableNextEpisodeAutoPlay").GetBoolean()
            && configuration.GetProperty("ResumeRewindSeconds").GetInt32() == 7
            && configuration.GetProperty("IntroSkipMode").GetString() == "AutoSkip",
            "Current-user reads return the configuration that was just written.");
        await StatusAsync(HttpMethod.Post, UserPath + "/Configuration", HttpStatusCode.BadRequest,
            "An invalid preference update is rejected atomically.", """{"ResumeRewindSeconds":61}""");
        check((await JsonAsync(UserPath)).GetProperty("Configuration").GetRawText() == configuration.GetRawText(),
            "Rejected preferences leave the previous configuration intact.");
        await StatusAsync(HttpMethod.Post, UserPath + "/Configuration", HttpStatusCode.NoContent,
            "The original user configuration can be restored.", originalConfiguration.GetRawText());

        var originalMetadata = await JsonAsync(UserPath + "/Items/1003");
        const string metadataBody = """
            {"Id":"1003","Name":"Lumen Contract Film","OriginalTitle":"Lumen Contract Original",
             "Overview":"Synthetic metadata contract round trip.","ProductionYear":2020,"OfficialRating":"PG",
             "Genres":["Harness Genre","Drama"],"Tags":["Harness"],"LockData":true,"LockedFields":["Overview"],
             "Type":"Person","ParentId":"collections","IsFolder":true,"RunTimeTicks":1234,
             "MediaSources":[{"Id":"forged","RunTimeTicks":1234}],
             "MediaStreams":[{"Index":99,"Type":"Video","Codec":"hevc"}]}
            """;
        await StatusAsync(HttpMethod.Post, "/emby/Items/1003", HttpStatusCode.NoContent,
            "Editable metadata updates are accepted.", metadataBody);
        var edited = await JsonAsync(UserPath + "/Items/1003");
        check(edited.GetProperty("Name").GetString() == "Lumen Contract Film"
            && edited.GetProperty("OriginalTitle").GetString() == "Lumen Contract Original"
            && edited.GetProperty("ProductionYear").GetInt32() == 2020
            && edited.GetProperty("LockData").GetBoolean()
            && edited.GetProperty("LockedFields")[0].GetString() == "Overview"
            && Type(edited) == "Movie" && edited.GetProperty("ParentId").GetString() == "movies"
            && !edited.GetProperty("IsFolder").GetBoolean() && edited.GetProperty("RunTimeTicks").GetInt64() == duration
            && edited.GetProperty("MediaSources").GetRawText() == originalMetadata.GetProperty("MediaSources").GetRawText(),
            "Metadata round trips preserve identity, ownership, and honest technical media fields.");
        var addedGenre = Items(await JsonAsync("/emby/Genres?UserId=" + UserId + "&ParentId=movies&SearchTerm=Harness%20Genre"));
        check(addedGenre.Length == 1 && edited.GetProperty("GenreItems").EnumerateArray().Any(pair =>
                pair.GetProperty("Id").GetInt64().ToString(CultureInfo.InvariantCulture) == Id(addedGenre[0]))
            && Ids(await JsonAsync(UserPath + "/Items?ParentId=movies&Genres=Harness%20Genre")).SequenceEqual(["1003"]),
            "Metadata-added genres remain resolvable and filterable by their honest numeric IDs.");
        await StatusAsync(HttpMethod.Post, "/emby/Items/1003", HttpStatusCode.BadRequest,
            "A metadata body cannot target a different item ID.", """{"Id":"1004","Name":"Must not apply"}""");
        check((await JsonAsync(UserPath + "/Items/1003")).GetProperty("Name").GetString() == "Lumen Contract Film",
            "Rejected metadata does not partially overwrite the item.");
        await StatusAsync(HttpMethod.Post, "/emby/Items/1003/Refresh", HttpStatusCode.NoContent,
            "Synthetic metadata refresh is an in-memory acknowledgement, not a media rewrite.", "{}");
        await StatusAsync(HttpMethod.Post, "/emby/Items/person-01", HttpStatusCode.Forbidden,
            "A non-editable person rejects metadata writes.", """{"Id":"person-01","Name":"Must not apply"}""");
        await StatusAsync(HttpMethod.Post, "/emby/Items/1003", HttpStatusCode.NoContent,
            "The original editable metadata can be restored.", MetadataRestoreBody(originalMetadata));
        check((await JsonAsync(UserPath + "/Items/1003")).GetProperty("Name").GetString() == originalMetadata.GetProperty("Name").GetString(),
            "Restoring metadata returns the original title.");
        var favorite = await JsonAsync(UserPath + "/FavoriteItems/1003", HttpMethod.Post);
        var unfavorite = await JsonAsync(UserPath + "/FavoriteItems/1003", HttpMethod.Delete);
        check(favorite.GetProperty("IsFavorite").GetBoolean() && !unfavorite.GetProperty("IsFavorite").GetBoolean(),
            "Favorite mutation responses round trip without changing item metadata.");

        var originalCollections = await JsonAsync(UserPath + "/Items?ParentId=collections&Limit=50");
        var membersBefore = Ids(await JsonAsync(UserPath + "/Items?ParentId=collection-night&Limit=50"));
        await StatusAsync(HttpMethod.Post, "/emby/Collections/collection-night/Items?Ids=1006,missing", HttpStatusCode.BadRequest,
            "A collection rejects a mixed valid-and-unknown member batch.");
        check(Ids(await JsonAsync(UserPath + "/Items?ParentId=collection-night&Limit=50")).ToHashSet(StringComparer.Ordinal).SetEquals(membersBefore),
            "Rejected collection additions are atomic.");
        await StatusAsync(HttpMethod.Post, "/emby/Collections?Name=Rejected&Ids=1006,missing", HttpStatusCode.BadRequest,
            "A collection cannot be created with an invalid member batch.");
        check(Ids(await JsonAsync(UserPath + "/Items?ParentId=collections&Limit=50")).ToHashSet(StringComparer.Ordinal).SetEquals(Ids(originalCollections)),
            "Rejected collection creation does not leave a partial collection.");
        var created = await JsonAsync("/emby/Collections?Name=Lumen%20Harness%20Collection&Ids=1003,1003,2000", HttpMethod.Post);
        var collectionId = Id(created);
        var collection = await JsonAsync(UserPath + "/Items/" + collectionId);
        check(collectionId.StartsWith("collection-created-", StringComparison.Ordinal) && Type(collection) == "BoxSet"
            && collection.GetProperty("ChildCount").GetInt32() == 2
            && collection.GetProperty("MovieCount").GetInt32() == 1 && collection.GetProperty("SeriesCount").GetInt32() == 1
            && Ids(await JsonAsync(UserPath + "/Items?ParentId=" + collectionId + "&Limit=50"))
                .ToHashSet(StringComparer.Ordinal).SetEquals(["1003", "2000"]),
            "Created collections deduplicate members and preserve movie/series counts.");
        await StatusAsync(HttpMethod.Post, "/emby/Collections/" + collectionId + "/Items?Ids=1004,1004", HttpStatusCode.NoContent,
            "Adding the same collection member twice succeeds without duplicates.");
        var addedMembers = Ids(await JsonAsync(UserPath + "/Items?ParentId=" + collectionId + "&Limit=50"));
        check(addedMembers.Length == 3 && addedMembers.ToHashSet(StringComparer.Ordinal).SetEquals(["1003", "1004", "2000"]),
            "Collection membership remains a set after repeated additions.");
        await StatusAsync(HttpMethod.Delete, "/emby/Collections/" + collectionId + "/Items?Ids=1003,missing", HttpStatusCode.BadRequest,
            "A collection rejects an invalid removal batch before changing valid members.");
        check(Ids(await JsonAsync(UserPath + "/Items?ParentId=" + collectionId + "&Limit=50")).ToHashSet(StringComparer.Ordinal).SetEquals(addedMembers),
            "Rejected collection removals are atomic.");
        await StatusAsync(HttpMethod.Delete, "/emby/Collections/" + collectionId + "/Items?Ids=1003", HttpStatusCode.NoContent,
            "A valid collection member removal succeeds.");
        check(Ids(await JsonAsync(UserPath + "/Items?ParentId=" + collectionId + "&Limit=50"))
                .ToHashSet(StringComparer.Ordinal).SetEquals(["1004", "2000"])
            && (await JsonAsync(UserPath + "/Items/1003")).GetProperty("ParentId").GetString() == "movies"
            && (await JsonAsync(UserPath + "/Items/2000")).GetProperty("ParentId").GetString() == "shows",
            "Collection additions and removals never reparent original media-library members.");

        await ImageAsync("/emby/Items/1001/Images/Primary", "p/515");
        await ImageAsync("/emby/Items/1001/Images/Backdrop/0", "b/515");
        await ImageAsync("/emby/Items/1001/Images/Thumb", "s/515");
        await ImageAsync("/emby/Items/1001/Images/Chapter/0", "s/515");
        await ImageAsync("/emby/Items/2115/Images/Primary", "s/541");
        await ImageAsync("/emby/Items/" + collectionId + "/Images/Primary", "p/525");
        await ImageAsync("/emby/Items/1001/Images/Primary", "p/515", head: true);
        foreach (var suffix in new[] { "Logo", "Primary/1", "Chapter/999", "Chapter/-1", "Primary/0/extra" })
            await StatusAsync(HttpMethod.Get, "/emby/Items/1001/Images/" + suffix, HttpStatusCode.NotFound,
                "Unsupported artwork variants return 404: " + suffix);
        await StatusAsync(HttpMethod.Get, "/emby/Items/1001/Images/Primary", HttpStatusCode.Unauthorized,
            "External artwork remains protected by fixture authentication.", authenticated: false);
        await StatusAsync(HttpMethod.Get, "/emby/Items/1001/Images/Primary?UserId=another-user", HttpStatusCode.Forbidden,
            "Artwork rejects a mismatched requested user.");

        var stats = await JsonAsync("/_fixture/stats");
        check(stats.GetProperty("LumenCatalog").GetBoolean() && stats.GetProperty("ExternalArtwork").GetBoolean()
            && stats.GetProperty("ActiveImages").GetInt32() == 0 && stats.GetProperty("HiddenResumeItems").GetInt32() == 0
            && !ContainsValue(stats, server.Token) && !ContainsValue(stats, testRoot)
            && !ContainsValue(stats, artworkDirectory) && !ContainsValue(stats, "Synthetic metadata contract round trip.")
            && stats.GetProperty("Queries").GetArrayLength() <= 200 && stats.GetProperty("Events").GetArrayLength() <= 200,
            "Lumen statistics expose only bounded synthetic observations, not tokens, bodies, or disk paths.");
        check(requestCount <= 120, $"Lumen contract checks remain bounded to {requestCount} HTTP requests after readiness.");

        async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body = null,
            RangeHeaderValue? range = null, bool authenticated = true)
        {
            if (++requestCount > 120) throw new InvalidOperationException("The Lumen HTTP request budget was exceeded.");
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.Range = range;
            return await (authenticated ? server.Client : anonymous).SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }

        async Task<JsonElement> JsonAsync(string path, HttpMethod? method = null, string? body = null)
        {
            using var response = await SendAsync(method ?? HttpMethod.Get, path, body);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.Clone();
        }

        async Task StatusAsync(HttpMethod method, string path, HttpStatusCode expected, string description,
            string? body = null, bool authenticated = true)
        {
            using var response = await SendAsync(method, path, body, authenticated: authenticated);
            check(response.StatusCode == expected, description);
        }

        async Task ImageAsync(string path, string key, bool head = false)
        {
            using var response = await SendAsync(head ? HttpMethod.Head : HttpMethod.Get, path);
            var expected = artwork[key];
            var tag = "lumen-jpeg-" + Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant()[..16];
            var actual = await response.Content.ReadAsByteArrayAsync();
            check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "image/jpeg"
                && response.Content.Headers.ContentLength == expected.Length && response.Headers.ETag?.Tag == '"' + tag + '"'
                && (head ? actual.Length == 0 : actual.SequenceEqual(expected)),
                "JPEG artwork preserves its mapped original bytes, MIME, length, and tag: " + key + (head ? " HEAD" : " GET"));
        }

        async Task RangeAsync(string path, int from, int to)
        {
            using var response = await SendAsync(HttpMethod.Get, path, range: new RangeHeaderValue(from, to));
            check(response.StatusCode == HttpStatusCode.PartialContent
                && response.Content.Headers.ContentType?.MediaType == "video/mp4"
                && response.Content.Headers.ContentRange?.From == from && response.Content.Headers.ContentRange?.To == to
                && response.Content.Headers.ContentRange?.Length == bytes.Length
                && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes[from..(to + 1)]),
                "Negotiated media ranges return the same original test bytes, including duplicate aliases.");
        }

        bool MeasuredSource(JsonElement source)
        {
            var streams = source.GetProperty("MediaStreams").EnumerateArray().ToArray();
            var video = streams.Single(stream => stream.GetProperty("Type").GetString() == "Video");
            var audio = streams.Single(stream => stream.GetProperty("Type").GetString() == "Audio");
            return source.GetProperty("Container").GetString() == "mp4" && source.GetProperty("Protocol").GetString() == "Http"
                && source.GetProperty("RunTimeTicks").GetInt64() == duration && source.GetProperty("Size").GetInt64() == bytes.Length
                && !source.GetProperty("SupportsDirectPlay").GetBoolean() && source.GetProperty("SupportsDirectStream").GetBoolean()
                && !source.GetProperty("SupportsTranscoding").GetBoolean() && streams.Length == 2
                && video.GetProperty("Codec").GetString() == "h264" && video.GetProperty("Width").GetInt32() == metadata.GetProperty("Width").GetInt32()
                && video.GetProperty("Height").GetInt32() == metadata.GetProperty("Height").GetInt32()
                && audio.GetProperty("Codec").GetString() == "aac" && audio.GetProperty("Channels").GetInt32() == metadata.GetProperty("AudioChannels").GetInt32()
                && audio.GetProperty("SampleRate").GetInt32() == metadata.GetProperty("AudioSampleRate").GetInt32();
        }
    }

    private static string Id(JsonElement item) => item.GetProperty("Id").GetString()!;
    private static string? Type(JsonElement item) => item.GetProperty("Type").GetString();
    private static JsonElement[] Items(JsonElement result) => result.GetProperty("Items").EnumerateArray().ToArray();
    private static string[] Ids(JsonElement result) => Items(result).Select(Id).ToArray();
    private static IEnumerable<string> GenreNames(JsonElement item) => item.GetProperty("Genres").EnumerateArray().Select(genre => genre.GetString()!);

    private static bool ValidSeason(JsonElement[] episodes, string seasonId, int number) => episodes.All(item =>
        Type(item) == "Episode" && item.GetProperty("SeriesId").GetString() == "2000"
        && item.GetProperty("SeasonId").GetString() == seasonId && item.GetProperty("ParentId").GetString() == seasonId
        && item.GetProperty("ParentIndexNumber").GetInt32() == number)
        && episodes.Select(item => item.GetProperty("IndexNumber").GetInt32()).SequenceEqual(Enumerable.Range(1, 6));

    private static bool ContainsValue(JsonElement value, string sensitive) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()?.Contains(sensitive, StringComparison.OrdinalIgnoreCase) == true,
        JsonValueKind.Object => value.EnumerateObject().Any(property => ContainsValue(property.Value, sensitive)),
        JsonValueKind.Array => value.EnumerateArray().Any(item => ContainsValue(item, sensitive)),
        _ => false
    };

    private static string MetadataRestoreBody(JsonElement item)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var property in item.EnumerateObject())
                if (property.Name is not ("LockData" or "LockedFields")) property.WriteTo(writer);
            writer.WritePropertyName("LockData");
            if (item.TryGetProperty("LockData", out var lockData)) lockData.WriteTo(writer);
            else writer.WriteBooleanValue(false);
            writer.WritePropertyName("LockedFields");
            if (item.TryGetProperty("LockedFields", out var fields)) fields.WriteTo(writer);
            else { writer.WriteStartArray(); writer.WriteEndArray(); }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task<Dictionary<string, byte[]>> CreateArtworkAsync(string root)
    {
        string[] posters =
        [
            "122", "227", "247", "249", "291", "331", "339", "345", "352", "375", "391", "396", "402", "404", "408", "419", "43",
            "443", "447", "453", "494", "515", "525", "541", "543", "545", "556", "563", "579", "58", "581", "594", "633", "639", "655",
            "660", "666", "670", "683", "685", "726", "729", "757", "778", "786", "797", "804", "826", "832", "838"
        ];
        string[] backdrops = ["227", "249", "515", "525", "541", "666", "683", "797"];
        string[] thumbs =
        ["158", "227", "249", "293", "404", "515", "525", "541", "552", "581", "594", "598", "612", "639", "655", "666", "683", "700", "797", "804", "841"];
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (kind, keys) in new[] { ("p", posters), ("b", backdrops), ("s", thumbs) })
        {
            var directory = Path.Combine(root, "assets", kind);
            Directory.CreateDirectory(directory);
            foreach (var key in keys)
            {
                var identity = kind + "/" + key;
                var image = TaggedJpeg(identity);
                await File.WriteAllBytesAsync(Path.Combine(directory, key + ".jpg"), image);
                result[identity] = image;
            }
        }
        return result;
    }

    private static byte[] TaggedJpeg(string identity)
    {
        // A JPEG comment distinguishes mapped assets without changing the one-pixel grayscale image.
        var comment = Encoding.ASCII.GetBytes("Synthetic Lumen contract artwork: " + identity);
        var result = new byte[TinyJpeg.Length + comment.Length + 4];
        TinyJpeg.AsSpan(0, 2).CopyTo(result);
        result[2] = 0xff; result[3] = 0xfe;
        result[4] = (byte)((comment.Length + 2) >> 8); result[5] = (byte)(comment.Length + 2);
        comment.CopyTo(result, 6);
        TinyJpeg.AsSpan(2).CopyTo(result.AsSpan(6 + comment.Length));
        return result;
    }

    private static readonly byte[] TinyJpeg = Convert.FromHexString(
        "FFD8FFE000104A46494600010100000100010000" +
        "FFDB004300" +
        "01010101010101010101010101010101" + "01010101010101010101010101010101" +
        "01010101010101010101010101010101" + "01010101010101010101010101010101" +
        "FFC0000B080001000101011100" +
        "FFC40014" + "00" + "01000000000000000000000000000000" + "00" +
        "FFC40014" + "10" + "01000000000000000000000000000000" + "00" +
        "FFDA0008010100003F003FFFD9");
}
