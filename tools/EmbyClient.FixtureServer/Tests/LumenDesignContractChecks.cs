using System.Net;
using System.Text;
using System.Text.Json;

internal static class LumenDesignContractChecks
{
    private const string UserPath = "/emby/Users/synthetic-user-demo";

    public static async Task RunAsync(string serverPath, string testRoot, byte[] bytes, Action<bool, string> check)
    {
        var artworkRoot = Path.GetFullPath(Path.Combine(testRoot, "lumen-design-handoff"));
        if (!string.Equals(Path.GetDirectoryName(artworkRoot), Path.GetFullPath(testRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The design artwork directory must remain inside this run's owned test directory.");
        var artwork = await CreateArtworkAsync(artworkRoot);
        await using var server = await TestServer.StartAsync(serverPath, testRoot, "--lumen-design-catalog", "--artwork-directory", artworkRoot);
        var requests = 0;
        var info = await JsonAsync("/emby/System/Info/Public");
        check(info.GetProperty("Id").GetString() == "synthetic-lumen-design-server-0001",
            "Design acceptance has its own synthetic identity and does not reuse the functional catalog's identity.");

        var movies = await JsonAsync(UserPath + "/Items?ParentId=movies&IncludeItemTypes=Movie&Limit=100");
        var series = await JsonAsync(UserPath + "/Items?ParentId=shows&IncludeItemTypes=Series&Limit=100");
        check(movies.GetProperty("TotalRecordCount").GetInt32() == 32 && series.GetProperty("TotalRecordCount").GetInt32() == 18,
            "Design acceptance exposes the handoff's actual 32 movies and 18 series, not its decorative large-library counts.");
        check(Ids(movies).Take(10).SequenceEqual(["1002", "1017", "1031", "1016", "1021", "1013", "1014", "1025", "1030", "1006"]),
            "Movie wall ordering follows the handoff's stable pinyin-initial sequence.");
        check(Ids(series).Take(10).SequenceEqual(["3600", "5000", "3400", "4200", "2800", "3800", "4800", "4000", "2000", "5400"]),
            "Series wall ordering follows the handoff's stable pinyin-initial sequence.");
        var movie = await JsonAsync(UserPath + "/Items/1001");
        var show = await JsonAsync(UserPath + "/Items/2000");
        check(movie.GetProperty("Name").GetString() == "\u5760\u843d\u4e4b\u5730"
            && movie.GetProperty("OriginalTitle").GetString() == "The Fallen Ground"
            && show.GetProperty("Name").GetString() == "\u6df1\u6d77\u56de\u54cd"
            && show.GetProperty("OriginalTitle").GetString() == "Echoes of the Deep",
            "Main detail screens retain Chinese handoff names and separate English original titles.");
        var cast = movie.GetProperty("People").EnumerateArray().ToArray();
        check(cast.Length == 9 && cast[0].GetProperty("Name").GetString() == "\u6797\u590f"
            && cast[0].GetProperty("Role").GetString() == "\u82cf\u9ece"
            && cast[6].GetProperty("Name").GetString() == "\u9648\u4e00\u9e23"
            && cast.All(person => !person.TryGetProperty("PrimaryImageTag", out _)),
            "Fictional cast names and roles match the handoff without advertising fabricated actor portraits.");
        check(cast.Select(person => person.GetProperty("Id").GetString()).SequenceEqual(
                ["person-01", "person-02", "person-03", "person-04", "person-05", "person-06", "person-07", "person-08", "person-09"])
            && show.GetProperty("People").EnumerateArray().Select(person => person.GetProperty("Id").GetString()).SequenceEqual(
                ["person-01", "person-02", "person-04", "person-03", "person-10", "person-05", "person-11", "person-08", "person-09"]),
            "Search-person credits leave both primary detail screens' original nine-person cast lists unchanged.");

        var searchTerm = Uri.EscapeDataString("\u591c");
        var people = await JsonAsync("/emby/Persons?UserId=synthetic-user-demo&SearchTerm=" + searchTerm + "&Limit=20");
        var searchPeople = Items(people);
        var expectedNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["person-12"] = "\u6797\u591c", ["person-13"] = "\u591c\u5ddd", ["person-14"] = "\u82cf\u591c\u767d"
        };
        check(Ids(people).ToHashSet(StringComparer.Ordinal).SetEquals(expectedNames.Keys)
            && searchPeople.All(person => person.GetProperty("Name").GetString() == expectedNames[person.GetProperty("Id").GetString()!]),
            "The normal Persons facet finds exactly the handoff's three night-search names, including Ye Chuan's original Chinese spelling.");
        var expectedWorks = new[] { (Person: "person-12", Title: "1002", Type: "Actor"),
            (Person: "person-13", Title: "1017", Type: "Director"), (Person: "person-14", Title: "1029", Type: "Actor") };
        check(expectedWorks.All(expected => Items(movies).Single(item => item.GetProperty("Id").GetString() == expected.Title)
                .GetProperty("People").EnumerateArray().Any(person => person.GetProperty("Id").GetString() == expected.Person
                    && person.GetProperty("Type").GetString() == expected.Type))
            && searchPeople.All(person =>
            {
                var id = person.GetProperty("Id").GetString();
                var movieCount = Items(movies).Count(item => item.GetProperty("People").EnumerateArray().Any(candidate => candidate.GetProperty("Id").GetString() == id));
                var seriesCount = Items(series).Count(item => item.GetProperty("People").EnumerateArray().Any(candidate => candidate.GetProperty("Id").GetString() == id));
                return movieCount == 1 && seriesCount == 0 && person.GetProperty("MovieCount").GetInt32() == movieCount
                    && person.GetProperty("SeriesCount").GetInt32() == seriesCount;
            }),
            "Search-person roles and one-film work counts are derived from real non-hero fixture credits rather than decorative handoff totals.");
        var moviePeople = await JsonAsync("/emby/Persons?UserId=synthetic-user-demo&ParentId=movies&IncludeItemTypes=Movie&SearchTerm=" + searchTerm);
        var seriesPeople = await JsonAsync("/emby/Persons?UserId=synthetic-user-demo&ParentId=shows&IncludeItemTypes=Series&SearchTerm=" + searchTerm);
        check(Ids(moviePeople).ToHashSet(StringComparer.Ordinal).SetEquals(expectedNames.Keys) && Items(seriesPeople).Length == 0,
            "Person facets retain their normal media-scope filtering; movie-only search credits do not leak into the series facet.");

        var latestMovies = await JsonAsync(UserPath + "/Items/Latest?ParentId=movies&IncludeItemTypes=Movie&Limit=10");
        var latestSeries = await JsonAsync(UserPath + "/Items/Latest?ParentId=shows&IncludeItemTypes=Series&Limit=10");
        check(Ids(latestMovies).SequenceEqual(["1004", "1027", "1001", "1012", "1029", "1024", "1006", "1014", "1019", "1010"]),
            "The design latest-movies shelf uses the exact handoff selection and order.");
        check(Ids(latestSeries).SequenceEqual(["2200", "2400", "5400", "4200", "2000", "4400", "3200", "5000", "3600", "2800"]),
            "The design latest-series shelf uses the exact handoff selection and order.");
        var createdOrder = await JsonAsync(UserPath + "/Items?ParentId=movies&IncludeItemTypes=Movie&SortBy=DateCreated&SortOrder=Descending&Limit=10");
        check(Ids(createdOrder).SequenceEqual(Ids(latestMovies)),
            "The expanded recently-added wall preserves the design shelf order without rewriting premiere dates.");
        var resume = await JsonAsync(UserPath + "/Items/Resume?MediaTypes=Video&Limit=20");
        check(Ids(resume).SequenceEqual(["2115", "1006", "2213", "2418", "1007"]),
            "The resume shelf has the handoff's five entries and ordering.");
        var favorites = await JsonAsync(UserPath + "/Items?IncludeItemTypes=Movie,Series&IsFavorite=true&Limit=100");
        check(Ids(favorites).ToHashSet(StringComparer.Ordinal).SetEquals(["1004", "2000", "1014", "2200", "1006", "2400", "1020", "3400", "1025", "4800", "1005", "4400"]),
            "Design favorites contain the handoff's twelve title selections.");

        var seasons = await JsonAsync("/emby/Shows/2000/Seasons?UserId=synthetic-user-demo");
        var episodes = await JsonAsync("/emby/Shows/2000/Episodes?UserId=synthetic-user-demo&SeasonId=2110");
        var secondSeason = Items(episodes);
        check(Ids(seasons).SequenceEqual(["2100", "2110", "2120"]) && secondSeason.Length == 10
            && secondSeason[4].GetProperty("Id").GetString() == "2115"
            && secondSeason[4].GetProperty("Name").GetString() == "\u9759\u9ed8\u533a"
            && secondSeason.Take(4).All(item => item.GetProperty("UserData").GetProperty("Played").GetBoolean())
            && secondSeason[4].GetProperty("UserData").GetProperty("PlayedPercentage").GetDouble() == 52,
            "Deep-sea season two matches its ten cards, four watched entries, and S2E5 resume state.");
        var next = await JsonAsync("/emby/Shows/NextUp?UserId=synthetic-user-demo&SeriesId=2000");
        check(Ids(next).SequenceEqual(["2115"]) && show.GetProperty("UserData").GetProperty("UnplayedItemCount").GetInt32() == 16,
            "NextUp and the unplayed count follow actual fixture state instead of the handoff's contradictory six-item badge.");

        using var metadata = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(testRoot, "fixture-h264-aac.json")));
        var duration = metadata.RootElement.GetProperty("DurationTicks").GetInt64();
        var source = movie.GetProperty("MediaSources")[0];
        var streams = source.GetProperty("MediaStreams").EnumerateArray().ToArray();
        check(movie.GetProperty("RunTimeTicks").GetInt64() == duration && secondSeason.All(item => item.GetProperty("RunTimeTicks").GetInt64() == duration)
            && source.GetProperty("Container").GetString() == "mp4"
            && streams.Single(item => item.GetProperty("Type").GetString() == "Video").GetProperty("Codec").GetString() == "h264"
            && streams.Single(item => item.GetProperty("Type").GetString() == "Audio").GetProperty("Codec").GetString() == "aac"
            && !source.GetProperty("SupportsTranscoding").GetBoolean(),
            "Design titles never substitute fictional film durations, HEVC, HDR, or unsupported transcoding for the real fixture source.");
        var session = await server.NegotiateAsync("2115");
        using var media = await server.GetAsync("/emby/Videos/2115/stream?PlaySessionId=" + Uri.EscapeDataString(session));
        check((await media.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes),
            "Design negotiation still streams the exact generated fixture bytes.");
        await ImageAsync("1001", "Primary", "p/515");
        await ImageAsync("1001", "Backdrop", "b/515");
        await ImageAsync("2115", "Primary", "s/541");
        await ImageAsync("2213", "Primary", "s/797");
        await ImageAsync("2418", "Primary", "s/666");
        await ImageAsync("5400", "Primary", "p/826");
        var stats = await server.StatsAsync();
        check(stats.GetProperty("LumenDesignCatalog").GetBoolean() && stats.GetProperty("LumenCatalog").GetBoolean()
            && stats.GetProperty("ExternalArtwork").GetBoolean() && !stats.GetRawText().Contains(server.Token, StringComparison.Ordinal)
            && !stats.GetRawText().Contains(artworkRoot, StringComparison.Ordinal) && requests <= 40,
            "Design mode is explicitly observable while its bounded receipt excludes credentials and disk paths.");

        async Task<JsonElement> JsonAsync(string path)
        {
            if (++requests > 40) throw new InvalidOperationException("The design catalog request budget was exceeded.");
            using var response = await server.GetAsync(path);
            response.EnsureSuccessStatusCode();
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return payload.RootElement.Clone();
        }

        async Task ImageAsync(string id, string kind, string expected)
        {
            if (++requests > 40) throw new InvalidOperationException("The design catalog request budget was exceeded.");
            using var response = await server.GetAsync("/emby/Items/" + id + "/Images/" + kind);
            check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "image/jpeg"
                && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(artwork[expected]),
                "Design artwork retains its exact numeric mapping: " + id + " / " + kind + " -> " + expected + ".");
        }
    }

    private static JsonElement[] Items(JsonElement result) => (result.ValueKind == JsonValueKind.Array ? result : result.GetProperty("Items")).EnumerateArray().ToArray();
    private static string[] Ids(JsonElement result) => Items(result).Select(item => item.GetProperty("Id").GetString()!).ToArray();

    private static async Task<Dictionary<string, byte[]>> CreateArtworkAsync(string root)
    {
        string[] posters =
        [
            "122", "227", "247", "249", "291", "331", "339", "345", "352", "375", "391", "396", "402", "404", "408", "419", "43",
            "443", "447", "453", "494", "515", "525", "541", "543", "545", "556", "563", "579", "58", "581", "594", "633", "639", "655",
            "660", "666", "670", "683", "685", "726", "729", "757", "778", "786", "797", "804", "826", "832", "838"
        ];
        string[] backdrops = ["227", "249", "515", "525", "541", "666", "683", "797"];
        string[] thumbs = ["158", "227", "249", "293", "404", "515", "525", "541", "552", "581", "594", "598", "612", "639", "655", "666", "683", "700", "797", "804", "841"];
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (kind, keys) in new[] { ("p", posters), ("b", backdrops), ("s", thumbs) })
        {
            var directory = Path.Combine(root, "assets", kind);
            Directory.CreateDirectory(directory);
            foreach (var key in keys)
            {
                // Distinct marker-bearing test bytes prove mapping only; this suite never decodes them as images.
                var identity = kind + "/" + key;
                var encoded = Encoding.ASCII.GetBytes(identity);
                var image = new byte[encoded.Length + 4];
                image[0] = 0xff; image[1] = 0xd8;
                encoded.CopyTo(image, 2);
                image[^2] = 0xff; image[^1] = 0xd9;
                await File.WriteAllBytesAsync(Path.Combine(directory, key + ".jpg"), image);
                result[identity] = image;
            }
        }
        return result;
    }
}
