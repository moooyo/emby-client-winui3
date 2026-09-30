using System.Globalization;
using EmbyClient.Api;

namespace EmbyClient.FixtureServer;

internal sealed partial class FixtureState
{
    private sealed record DesignTitle(int Id, string Art, string Name, string EnglishName, int Year,
        string Genre, double Rating, string Initial, int Seasons = 0, int EpisodesPerSeason = 10);

    private static readonly DesignTitle[] DesignMovies =
    [
        new(1001, "515", "\u5760\u843d\u4e4b\u5730", "The Fallen Ground", 2025, "\u79d1\u5e7b", 8.1, "Z"),
        new(1002, "249", "\u4e0d\u591c\u57ce", "Sleepless City", 2024, "\u5267\u60c5", 7.8, "B"),
        new(1003, "525", "\u6c99\u4e18\u4e4b\u5916", "Beyond the Dunes", 2023, "\u5192\u9669", 7.5, "S"),
        new(1004, "683", "\u661f\u8f68", "Star Trails", 2026, "\u79d1\u5e7b", 8.4, "X"),
        new(1005, "227", "\u96fe\u4e2d\u5217\u8f66", "Train in the Mist", 2022, "\u60ac\u7591", 7.9, "W"),
        new(1006, "804", "\u5f52\u9014", "The Way Home", 2024, "\u5267\u60c5", 8.2, "G"),
        new(1007, "655", "\u84dd\u8272\u9762\u5305\u8f66", "Blue Van", 2023, "\u559c\u5267", 7.2, "L"),
        new(1008, "633", "\u6797\u4e2d\u4eba", "The Forest Guest", 2022, "\u60ca\u609a", 6.9, "L"),
        new(1009, "778", "\u590f\u81f3", "Summer Solstice", 2021, "\u7231\u60c5", 7.4, "X"),
        new(1010, "660", "\u70df\u706b", "Fireworks", 2024, "\u7231\u60c5", 7.6, "Y"),
        new(1011, "726", "\u96ea\u7ebf", "Snowline", 2023, "\u5267\u60c5", 8.0, "X"),
        new(1012, "375", "\u65e0\u540d\u8005", "The Nameless", 2025, "\u52a8\u4f5c", 7.1, "W"),
        new(1013, "331", "\u72ec\u884c\u8005", "The Lone Walker", 2021, "\u72af\u7f6a", 7.7, "D"),
        new(1014, "494", "\u7b2c\u4e5d\u7ad9", "The Ninth Station", 2024, "\u60ac\u7591", 8.3, "D"),
        new(1015, "579", "\u9713\u8679\u4e4b\u4e0b", "Under Neon", 2023, "\u72af\u7f6a", 7.3, "N"),
        new(1016, "247", "\u8d64\u5ca9", "Red Rock", 2022, "\u897f\u90e8", 7.0, "C"),
        new(1017, "545", "\u5e03\u62c9\u683c\u4e4b\u591c", "A Night in Prague", 2020, "\u7231\u60c5", 7.5, "B"),
        new(1018, "556", "\u65e7\u65e5\u8c37", "Old Valley", 2021, "\u6050\u6016", 6.8, "J"),
        new(1019, "447", "\u8fdc\u5c71", "Distant Mountains", 2024, "\u5267\u60c5", 8.1, "Y"),
        new(1020, "581", "\u6df1\u84dd", "Deep Blue", 2023, "\u7eaa\u5f55", 8.8, "S"),
        new(1021, "563", "\u957f\u8def", "The Long Road", 2022, "\u516c\u8def", 7.6, "C"),
        new(1022, "122", "\u4f26\u6566\u8ff7\u96fe", "London Fog", 2019, "\u60ac\u7591", 7.4, "L"),
        new(1023, "453", "\u56de\u58f0", "Echo", 2024, "\u97f3\u4e50", 8.0, "H"),
        new(1024, "832", "\u5979\u7684\u4e66\u623f", "Her Reading Room", 2025, "\u5267\u60c5", 7.9, "T"),
        new(1025, "838", "\u7236\u4eb2\u7684\u57ce", "A Father's City", 2023, "\u5bb6\u5ead", 8.2, "F"),
        new(1026, "408", "\u672b\u73ed\u8f66", "Last Bus", 2024, "\u5267\u60c5", 7.7, "M"),
        new(1027, "396", "\u96a7\u9053", "The Tunnel", 2026, "\u79d1\u5e7b", 7.5, "S"),
        new(1028, "402", "\u843d\u65e5\u5927\u9053", "Sunset Boulevard", 2021, "\u7231\u60c5", 7.2, "L"),
        new(1029, "352", "\u591c\u884c\u8005", "Nightwalker", 2025, "\u52a8\u4f5c", 7.8, "Y"),
        new(1030, "404", "\u98ce\u66b4\u773c", "Eye of the Storm", 2024, "\u707e\u96be", 7.0, "F"),
        new(1031, "786", "\u5317\u7eac\u516d\u5341", "Sixty Degrees North", 2023, "\u5267\u60c5", 8.0, "B"),
        new(1032, "757", "\u65c5\u7a0b", "The Journey", 2022, "\u516c\u8def", 7.3, "L")
    ];

    private static readonly DesignTitle[] DesignSeries =
    [
        new(2000, "541", "\u6df1\u6d77\u56de\u54cd", "Echoes of the Deep", 2024, "\u79d1\u5e7b", 8.6, "S", 3),
        new(2200, "797", "\u96e8\u591c\u8ffd\u51f6", "Rain Night", 2026, "\u72af\u7f6a", 8.4, "Y", 1, 24),
        new(2400, "666", "\u7af9\u6797\u5ba2", "Bamboo Wanderer", 2025, "\u6b66\u4fa0", 8.1, "Z", 2, 18),
        new(2600, "670", "\u4f0a\u65af\u5766\u5e03\u5c14\u6765\u4fe1", "Letters from Istanbul", 2023, "\u5267\u60c5", 7.9, "Y", 1),
        new(3200, "685", "\u4e91\u4e0a", "Above the Clouds", 2024, "\u5947\u5e7b", 7.6, "Y", 2),
        new(2800, "291", "\u8ff7\u9014", "Lost Bearings", 2022, "\u60ac\u7591", 7.8, "M", 1),
        new(3000, "543", "\u96fe\u6797", "Fog Forest", 2023, "\u60ca\u609a", 7.4, "W", 2),
        new(3400, "43", "\u9ed1\u767d\u57ce", "Black and White City", 2021, "\u72af\u7f6a", 8.5, "H", 4),
        new(3600, "58", "\u706f\u5854", "The Lighthouse", 2024, "\u5267\u60c5", 7.7, "D", 1),
        new(3800, "339", "\u672b\u65e5\u516c\u8def", "The Last Highway", 2022, "\u79d1\u5e7b", 7.2, "M", 2),
        new(4000, "443", "\u7fa4\u5c71\u4e4b\u95f4", "Between the Mountains", 2023, "\u5267\u60c5", 8.0, "Q", 3),
        new(4200, "419", "\u65e7\u8857", "Old Street", 2025, "\u5bb6\u5ead", 7.5, "J", 1),
        new(4400, "594", "\u591c\u822a", "Night Flight", 2024, "\u60ac\u7591", 7.9, "Y", 1),
        new(4600, "391", "\u5929\u9645\u7ebf", "Skyline", 2023, "\u90fd\u5e02", 7.1, "T", 2),
        new(4800, "729", "\u7267\u573a\u5f80\u4e8b", "Ranch Memories", 2021, "\u5e74\u4ee3", 8.2, "M", 3),
        new(5000, "345", "\u5730\u4e0b", "Underground", 2024, "\u60ca\u609a", 7.3, "D", 1),
        new(5200, "639", "\u6e38\u4e50\u56ed", "The Amusement Park", 2022, "\u559c\u5267", 7.0, "Y", 2),
        new(5400, "826", "\u6c34\u57ce", "Water City", 2025, "\u7231\u60c5", 7.6, "S", 1)
    ];

    private static readonly string[] DesignGenres =
    [
        "\u52a8\u4f5c", "\u79d1\u5e7b", "\u60ac\u7591", "\u5267\u60c5", "\u7231\u60c5", "\u559c\u5267", "\u72af\u7f6a",
        "\u7eaa\u5f55", "\u52a8\u753b", "\u5192\u9669", "\u897f\u90e8", "\u6050\u6016", "\u516c\u8def", "\u97f3\u4e50",
        "\u5bb6\u5ead", "\u707e\u96be", "\u6b66\u4fa0", "\u53e4\u88c5", "\u5947\u5e7b", "\u60ca\u609a", "\u90fd\u5e02", "\u5e74\u4ee3"
    ];
    private static readonly (string Name, string Type)[] DesignPeople =
    [
        ("\u6797\u590f", "Actor"), ("\u5468\u91ce", "Actor"), ("\u4f55\u5ddd", "Actor"), ("\u6c5f\u665a", "Actor"),
        ("\u987e\u5357", "Actor"), ("\u8bb8\u9ed8", "Actor"), ("\u9648\u4e00\u9e23", "Director"), ("\u53f6\u77e5\u79cb", "Writer"),
        ("\u767d\u821f", "Cinematographer"), ("\u6c88\u542c", "Actor"), ("\u4e54\u5b89", "Director"),
        ("\u6797\u591c", "Actor"), ("\u591c\u5ddd", "Director"), ("\u82cf\u591c\u767d", "Actor")
    ];
    private static readonly (string Art, string Name, string Synopsis)[] DesignEpisodes =
    [
        ("700", "\u6f6e\u6c50\u4e4b\u4e0b", "\u5730\u9707\u540e\u7684\u7b2c\u4e03\u5929\uff0c\u201c\u56de\u58f0\u53f7\u201d\u7b2c\u4e00\u6b21\u6536\u5230\u4e86\u6765\u81ea\u6d77\u5e95\u7684\u89c4\u5f8b\u4fe1\u53f7\u3002"),
        ("612", "\u88c2\u7f1d", "\u5916\u58f3\u51fa\u73b0\u88c2\u7f1d\uff0c\u961f\u957f\u51b3\u5b9a\u5c01\u95ed C \u8231\uff0c\u4f46\u6709\u4eba\u62d2\u7edd\u79bb\u5f00\u3002"),
        ("841", "\u56de\u58f0", "\u6f5c\u6c34\u5458\u5728\u6d77\u6c9f\u8fb9\u7f18\uff0c\u542c\u89c1\u4e86\u4e03\u5e74\u524d\u9047\u96be\u961f\u5458\u7684\u58f0\u97f3\u3002"),
        ("552", "\u5931\u8054", "\u8865\u7ed9\u8239\u5728\u6d53\u96fe\u4e2d\u5931\u53bb\u8054\u7cfb\uff0c\u7814\u7a76\u7ad9\u53ea\u5269\u6700\u540e 72 \u5c0f\u65f6\u7684\u6c27\u6c14\u3002"),
        ("541", "\u9759\u9ed8\u533a", "\u4e3a\u4e86\u8ffd\u8e2a\u4fe1\u53f7\u6e90\uff0c\u6797\u590f\u72ec\u81ea\u4e0b\u6f5c\uff0c\u8fdb\u5165\u58f0\u5450\u65e0\u6cd5\u8986\u76d6\u7684\u9759\u9ed8\u533a\u3002"),
        ("581", "\u53d1\u5149\u4f53", "\u6df1\u6d77\u4e2d\u51fa\u73b0\u6210\u7fa4\u7684\u53d1\u5149\u751f\u7269\uff0c\u5b83\u4eec\u4f3c\u4e4e\u5728\u56de\u5e94\u67d0\u79cd\u6307\u4ee4\u3002"),
        ("293", "\u96fe\u6e2f", "\u5e78\u5b58\u8005\u62b5\u8fbe\u5e9f\u5f03\u6e2f\u53e3\uff0c\u5374\u5728\u7801\u5934\u4e0a\u770b\u5230\u4e86\u201c\u56de\u58f0\u53f7\u201d\u7684\u6551\u751f\u8247\u3002"),
        ("594", "\u591c\u6f5c", "\u4e00\u6b21\u591c\u95f4\u4e0b\u6f5c\uff0c\u63ed\u5f00\u4e86\u7814\u7a76\u7ad9\u5efa\u7acb\u4e4b\u521d\u88ab\u9690\u7792\u7684\u771f\u6b63\u76ee\u7684\u3002"),
        ("404", "\u98ce\u66b4\u773c", "\u98ce\u66b4\u903c\u8fd1\uff0c\u6240\u6709\u4eba\u5fc5\u987b\u5728\u64a4\u79bb\u4e0e\u771f\u76f8\u4e4b\u95f4\u505a\u51fa\u9009\u62e9\u3002"),
        ("598", "\u6845\u6746", "\u7b2c\u4e8c\u5b63\u7ec8\u7ae0\uff1a\u4fe1\u53f7\u7684\u6e90\u5934\uff0c\u7ec8\u4e8e\u6d6e\u51fa\u6c34\u9762\u3002")
    ];
    private static readonly string[] DesignLatestMovies = ["1004", "1027", "1001", "1012", "1029", "1024", "1006", "1014", "1019", "1010"];
    private static readonly string[] DesignLatestSeries = ["2200", "2400", "5400", "4200", "2000", "4400", "3200", "5000", "3600", "2800"];
    private readonly Dictionary<string, int> _designLatestRanks = new(StringComparer.Ordinal);

    private IEnumerable<BaseItemDto> CreateLumenDesignCatalog()
    {
        var catalog = new List<BaseItemDto>();
        catalog.Add(DesignItem("movies", "\u7535\u5f71", "CollectionFolder", null, "515", "515", "515", landscape: true)
            with { CollectionType = "movies", ChildCount = DesignMovies.Length, MovieCount = DesignMovies.Length });
        catalog.Add(DesignItem("shows", "\u5267\u96c6", "CollectionFolder", null, "797", "797", "797", landscape: true)
            with { CollectionType = "tvshows", ChildCount = DesignSeries.Length, SeriesCount = DesignSeries.Length });
        catalog.Add(DesignItem("collections", "\u5408\u96c6", "CollectionFolder", null, "249", "249", "249", landscape: true)
            with { CollectionType = "boxsets", ChildCount = 3 });

        foreach (var (spec, index) in DesignMovies.Select((spec, index) => (spec, index)))
            catalog.Add(Title(spec, index, "Movie", "movies") with { LocalTrailerCount = spec.Id is 1001 or 1002 ? 1 : 0 });
        foreach (var (spec, index) in DesignSeries.Select((spec, index) => (spec, index)))
        {
            var series = Title(spec, index, "Series", "shows") with
            {
                ChildCount = spec.Seasons, SeasonCount = spec.Seasons,
                RecursiveItemCount = spec.Seasons * (spec.EpisodesPerSeason + 1), LocalTrailerCount = spec.Id == 2000 ? 1 : 0
            };
            catalog.Add(series);
            for (var season = 1; season <= spec.Seasons; season++)
            {
                var seasonId = DesignSeasonId(spec.Id, season);
                var year = spec.Id == 2000 ? season == 1 ? 2023 : 2024 : Math.Min(2026, spec.Year + season - 1);
                catalog.Add(DesignItem(seasonId, "\u7b2c " + season + " \u5b63", "Season", series.Id, spec.Art,
                    DesignBackdrop(spec.Art, true), DesignThumb(spec.Art, true)) with
                {
                    SeriesId = series.Id, SeriesName = series.Name, IndexNumber = season, ChildCount = spec.EpisodesPerSeason,
                    RecursiveItemCount = spec.EpisodesPerSeason, ProductionYear = year, Genres = series.Genres, GenreItems = series.GenreItems
                });
                for (var episode = 1; episode <= spec.EpisodesPerSeason; episode++)
                {
                    var episodeId = DesignEpisodeId(spec.Id, season, episode);
                    var episodeSpec = DesignEpisodes[(episode - 1 + (season == 2 ? 0 : (season - 1) * 3)) % DesignEpisodes.Length];
                    var art = spec.Id == 2000 ? episodeSpec.Art : DesignThumb(spec.Art, true);
                    var name = spec.Id == 2200 && episode == 3 ? "\u7b2c\u4e09\u4e2a\u76ee\u51fb\u8005"
                        : spec.Id == 2400 && episode == 8 ? "\u95ee\u5251" : episodeSpec.Name;
                    catalog.Add(DesignItem(episodeId, name, "Episode", seasonId, art, DesignBackdrop(spec.Art, true), art, landscape: true) with
                    {
                        SeriesId = series.Id, SeriesName = series.Name, SeasonId = seasonId, IndexNumber = episode, ParentIndexNumber = season,
                        SortName = episode.ToString("D2", CultureInfo.InvariantCulture), ProductionYear = year,
                        PremiereDate = new DateTimeOffset(year, 3, 1, 0, 0, 0, TimeSpan.Zero).AddDays((episode - 1) * 7),
                        Overview = spec.Id == 2000 ? episodeSpec.Synopsis : DesignSynopsis(spec),
                        Genres = series.Genres, GenreItems = series.GenreItems, People = DesignCast(true),
                        CommunityRating = spec.Rating, OfficialRating = "16+", Tags = ["Synthetic", "DesignAcceptance"]
                    });
                }
            }
        }

        AddCollection("collection-night", "\u591c\u8272\u4e09\u90e8\u66f2", ["1002", "1015", "1029"], "249");
        AddCollection("collection-frontier", "\u5730\u5e73\u7ebf\u4e4b\u5916", ["1001", "1003", "1004", "1011"], "515");
        AddCollection("collection-home", "\u5f52\u5bb6\u4e4b\u8def", ["1006", "1025", "1032"], "804");
        foreach (var parentId in new[] { "1001", "1002", "2000" })
        {
            var parent = catalog.Single(item => item.Id == parentId);
            var selection = _artworkSelections[parentId];
            var id = "trailer-" + parentId;
            catalog.Add(DesignItem(id, parent.Name + " - Synthetic Trailer", "Trailer", parentId,
                selection.Thumb, selection.Backdrop, selection.Thumb, landscape: true) with
            {
                Overview = parent.Overview, ProductionYear = parent.ProductionYear, Genres = parent.Genres, GenreItems = parent.GenreItems
            });
            _localTrailers[parentId] = [id];
        }
        for (var index = 0; index < DesignPeople.Length; index++)
        {
            var id = PersonId(index);
            var person = DesignPeople[index];
            var works = catalog.Where(item => item.Type is "Movie" or "Series" && item.People?.Any(candidate => candidate.Id == id) == true).ToArray();
            catalog.Add(new BaseItemDto
            {
                Id = id, Name = person.Name, SortName = person.Name, Type = "Person", IsFolder = false,
                Overview = "Fictional design handoff cast member. No real person's biography or portrait is represented.",
                MovieCount = works.Count(item => item.Type == "Movie"), SeriesCount = works.Count(item => item.Type == "Series"),
                PrimaryImageAspectRatio = 1, CanEditItems = false
            });
        }
        for (var index = 0; index < DesignGenres.Length; index++)
        {
            var genre = DesignGenres[index];
            var works = catalog.Where(item => item.Type is "Movie" or "Series" && item.Genres?.Contains(genre, StringComparer.Ordinal) == true).ToArray();
            var art = works.Length > 0 ? _artworkSelections[works[0].Id!].Primary : "515";
            catalog.Add(DesignItem(GenreId(index), genre, "Genre", null, art, DesignBackdrop(art, false), DesignThumb(art, false)) with
            {
                Overview = "Fictional design handoff genre.", MovieCount = works.Count(item => item.Type == "Movie"),
                SeriesCount = works.Count(item => item.Type == "Series"), CanEditItems = false
            });
        }
        SetLatestRanks(DesignLatestMovies, DesignMovies.Select(spec => spec.Id.ToString(CultureInfo.InvariantCulture)));
        SetLatestRanks(DesignLatestSeries, DesignSeries.Select(spec => spec.Id.ToString(CultureInfo.InvariantCulture)));
        return catalog;

        BaseItemDto Title(DesignTitle spec, int index, string type, string parent)
        {
            var genres = spec.Id == 1001 ? new[] { spec.Genre, "\u5192\u9669", "\u60ac\u7591" }
                : spec.Id is 2000 or 2200 ? new[] { spec.Genre, "\u60ac\u7591" }
                : spec.Id == 2400 ? new[] { spec.Genre, "\u53e4\u88c5" } : [spec.Genre];
            return DesignItem(spec.Id.ToString(CultureInfo.InvariantCulture), spec.Name, type, parent, spec.Art,
                DesignBackdrop(spec.Art, type == "Series"), DesignThumb(spec.Art, type == "Series")) with
            {
                OriginalTitle = spec.EnglishName, SortName = spec.Initial + "-" + index.ToString("D2", CultureInfo.InvariantCulture),
                ProductionYear = spec.Year, PremiereDate = new DateTimeOffset(spec.Year, 2, 1, 0, 0, 0, TimeSpan.Zero),
                Overview = DesignSynopsis(spec), CommunityRating = spec.Rating,
                OfficialRating = type == "Series" ? "16+" : "12+", Genres = genres, GenreItems = DesignGenrePairs(genres),
                People = DesignCast(type == "Series", spec.Id), Tags = spec.Id == 1015 ? ["Synthetic", "DesignAcceptance", "\u591c\u666f"] : ["Synthetic", "DesignAcceptance"]
            };
        }

        void AddCollection(string id, string name, string[] members, string art)
        {
            _collectionMembers[id] = members.ToHashSet(StringComparer.Ordinal);
            catalog.Add(DesignItem(id, name, "BoxSet", "collections", art, DesignBackdrop(art, false), DesignThumb(art, false)) with
            {
                ChildCount = members.Length, MovieCount = members.Length, Tags = ["Synthetic", "DesignAcceptance"],
                Overview = "A fictional collection for design acceptance only."
            });
        }

        void SetLatestRanks(string[] preferred, IEnumerable<string> fallback)
        {
            var rank = 0;
            foreach (var id in preferred.Concat(fallback).Distinct(StringComparer.Ordinal)) _designLatestRanks[id] = rank++;
        }
    }

    private BaseItemDto DesignItem(string id, string name, string type, string? parent, string art,
        string? backdrop = null, string? thumb = null, bool landscape = false) =>
        LumenItem(id, name, type, parent, art, backdrop, thumb, landscape) with { Overview = null };

    private static string DesignBackdrop(string art, bool series) => LumenBackdrops.Contains(art, StringComparer.Ordinal) ? art : series ? "541" : "515";
    private static string DesignThumb(string art, bool series) => LumenThumbs.Contains(art, StringComparer.Ordinal) ? art : series ? "541" : "515";
    private static string DesignSeasonId(int seriesId, int season) => (seriesId == 2000 ? 2100 + (season - 1) * 10 : seriesId + season * 10).ToString(CultureInfo.InvariantCulture);
    private static string DesignEpisodeId(int seriesId, int season, int episode)
    {
        var seasonId = DesignSeasonId(seriesId, season);
        return episode < 10 ? (int.Parse(seasonId, CultureInfo.InvariantCulture) + episode).ToString(CultureInfo.InvariantCulture)
            : seasonId + "-episode-" + episode.ToString("D2", CultureInfo.InvariantCulture);
    }
    private int DesignLatestRank(BaseItemDto item) => _designLatestRanks.GetValueOrDefault(item.SeriesId ?? item.Id!, int.MaxValue);
    private static NameLongIdPair[] DesignGenrePairs(string[] genres) => genres.Select(genre => new NameLongIdPair
    {
        Name = genre, Id = 50001 + Array.IndexOf(DesignGenres, genre)
    }).ToArray();

    private static PersonInfo[] DesignCast(bool series, int? titleId = null)
    {
        int[] indices = series ? [0, 1, 3, 2, 9, 4, 10, 7, 8] : [0, 1, 2, 3, 4, 5, 6, 7, 8];
        string?[] roles = series
            ? ["\u6797\u6f84", "\u97e9\u5c7f", "\u82cf\u82ae", "\u8001\u5468", "\u963f\u5c9a", "\u9646\u821f", null, null, "\u6444\u5f71\u6307\u5bfc"]
            : ["\u82cf\u9ece", "\u9648\u9ed8", "\u8001 K", "\u963f\u96ea", "\u673a\u957f", "\u8c03\u67e5\u5458", null, null, "\u6444\u5f71\u6307\u5bfc"];
        var cast = indices.Select((index, offset) => new PersonInfo
        {
            Id = PersonId(index), Name = DesignPeople[index].Name, Type = DesignPeople[index].Type, Role = roles[offset]
        }).ToArray();
        var extraIndex = titleId switch { 1002 => 11, 1017 => 12, 1029 => 13, _ => -1 };
        if (extraIndex < 0) return cast;
        return [.. cast, new PersonInfo
        {
            Id = PersonId(extraIndex), Name = DesignPeople[extraIndex].Name, Type = DesignPeople[extraIndex].Type,
            Role = titleId == 1002 ? "\u4e58\u5ba2" : titleId == 1029 ? "\u76ee\u51fb\u8005" : null
        }];
    }

    private static string DesignSynopsis(DesignTitle spec) => spec.Id switch
    {
        1001 => "\u4e00\u67b6\u5931\u8054\u4e8c\u5341\u5e74\u7684\u5ba2\u673a\u6b8b\u9ab8\u5728\u51b0\u5c9b\u9ed1\u6c99\u6ee9\u91cd\u73b0\uff0c\u673a\u8231\u91cc\u7684\u65f6\u949f\u4ecd\u5728\u8d70\u52a8\u3002\u8c03\u67e5\u5458\u82cf\u9ece\u5e26\u961f\u8fdb\u5165\u7981\u533a\uff0c\u5374\u53d1\u73b0\u6bcf\u4e00\u4f4d\u4e58\u5ba2\u90fd\u5728\u540c\u4e00\u5929\u201c\u56de\u6765\u201d\u4e86\u2014\u2014\u800c\u4ed6\u4eec\u7684\u8bb0\u5fc6\uff0c\u505c\u5728\u5760\u843d\u524d\u7684\u6700\u540e\u4e00\u5206\u949f\u3002",
        1002 => "\u51cc\u6668\u4e09\u70b9\u7684\u5927\u6865\u4e0a\uff0c\u51fa\u79df\u8f66\u53f8\u673a\u4e0e\u4e00\u4f4d\u4e0d\u80af\u8bf4\u51fa\u76ee\u7684\u5730\u7684\u4e58\u5ba2\uff0c\u7a7f\u8fc7\u6574\u5ea7\u4e0d\u7720\u7684\u57ce\u5e02\u3002\u4e00\u591c\u4e4b\u95f4\uff0c\u4e24\u4e2a\u964c\u751f\u4eba\u4ea4\u6362\u4e86\u5404\u81ea\u6700\u6df1\u7684\u79d8\u5bc6\u3002",
        1004 => "\u5929\u6587\u5b66\u5bb6\u5728\u4e00\u5f20\u957f\u66dd\u5149\u7167\u7247\u91cc\u53d1\u73b0\u4e86\u4e00\u6761\u4e0d\u8be5\u5b58\u5728\u7684\u661f\u8f68\u3002\u4e3a\u4e86\u627e\u5230\u5b83\u7684\u6765\u6e90\uff0c\u5979\u5fc5\u987b\u5728\u6781\u591c\u964d\u4e34\u4e4b\u524d\uff0c\u767b\u4e0a\u5317\u6781\u5708\u5185\u6700\u540e\u4e00\u5ea7\u89c2\u6d4b\u7ad9\u3002",
        2000 => "\u6df1\u6d77\u7814\u7a76\u7ad9\u201c\u56de\u58f0\u53f7\u201d\u5728\u4e00\u6b21\u5730\u9707\u540e\u4e0e\u5916\u754c\u5931\u8054\u3002\u5e78\u5b58\u8005\u6536\u5230\u4e00\u6bb5\u6765\u81ea\u6d77\u5e95\u7684\u6c42\u6551\u4fe1\u53f7\u2014\u2014\u800c\u53d1\u51fa\u4fe1\u53f7\u7684\uff0c\u662f\u4e03\u5e74\u524d\u5df2\u7ecf\u9047\u96be\u7684\u961f\u5458\u3002",
        2200 => "\u66fe\u4efb\u5211\u8b66\u7684\u4ea4\u8b66\u674e\u661f\u8fb0\u5728\u96e8\u591c\u63a5\u624b\u4e00\u6869\u79bb\u5947\u8f66\u7978\uff0c\u6b7b\u8005\u8eab\u4efd\u6210\u8c1c\uff0c\u73b0\u573a\u53c8\u906d\u4eba\u523b\u610f\u7834\u574f\u3002\u968f\u7740\u4e13\u6848\u7ec4\u6df1\u5165\u8c03\u67e5\uff0c\u4e00\u573a\u770b\u4f3c\u666e\u901a\u7684\u4ea4\u901a\u4e8b\u6545\u80cc\u540e\uff0c\u6d6e\u73b0\u51fa\u7275\u8fde\u4e8c\u5341\u5e74\u7684\u65e7\u6848\u3002",
        2400 => "\u6c5f\u6e56\u4f20\u8a00\uff0c\u7af9\u6797\u6df1\u5904\u4f4f\u7740\u4e00\u4f4d\u4e0d\u95ee\u4e16\u4e8b\u7684\u5251\u5ba2\u3002\u5f53\u671d\u5ef7\u5bc6\u4f7f\u5e26\u7740\u4e00\u7eb8\u8840\u4e66\u627e\u4e0a\u95e8\u6765\uff0c\u6c89\u5bc2\u5341\u5e74\u7684\u5251\uff0c\u7ec8\u4e8e\u8981\u518d\u6b21\u51fa\u9798\u3002",
        _ => "\u4e00\u6bb5\u5173\u4e8e\u76f8\u9047\u3001\u9009\u62e9\u4e0e\u53d1\u73b0\u7684\u865a\u6784\u6545\u4e8b\u3002\u65c5\u9014\u4e2d\u7684\u4eba\u4eec\u8ffd\u5bfb\u88ab\u9057\u5fd8\u7684\u7ebf\u7d22\uff0c\u4e5f\u9010\u6e10\u627e\u5230\u5f7c\u6b64\u4e0e\u81ea\u5df1\u7684\u7b54\u6848\u3002"
    };

    private void SeedLumenDesignUserData()
    {
        foreach (var id in _userData.Keys.ToArray()) _userData[id] = _userData[id] with { PlaybackPositionTicks = 0 };
        foreach (var id in new[] { "1004", "2000", "1014", "2200", "1006", "2400", "1020", "3400", "1025", "4800", "1005", "4400" })
            _userData[id] = _userData[id] with { IsFavorite = true };
        foreach (var id in new[] { "1002", "1011", "1013", "1022", "1025", "1023" }) MarkPlayed(id);
        foreach (var episode in Enumerable.Range(1, 10)) MarkPlayed(DesignEpisodeId(2000, 1, episode));
        foreach (var episode in Enumerable.Range(1, 4)) MarkPlayed(DesignEpisodeId(2000, 2, episode));
        foreach (var episode in Enumerable.Range(1, 2)) MarkPlayed(DesignEpisodeId(2200, 1, episode));
        foreach (var episode in Enumerable.Range(1, 7)) MarkPlayed(DesignEpisodeId(2400, 1, episode));
        foreach (var seriesId in new[] { 3400, 4800 })
            foreach (var item in _items.Values.Where(item => item.SeriesId == seriesId.ToString(CultureInfo.InvariantCulture) && item.Type == "Episode")) MarkPlayed(item.Id!);
        _userData["3400"] = _userData["3400"] with { Played = true, PlayCount = 1 };
        _userData["4800"] = _userData["4800"] with { Played = true, PlayCount = 1 };

        var resume = new[] { ("2115", .52), ("1006", .38), ("2213", .30), ("2418", .78), ("1007", .14), ("1001", .36), ("1014", .66) };
        for (var index = 0; index < resume.Length; index++)
        {
            var (id, percentage) = resume[index];
            _userData[id] = _userData[id] with
            {
                PlaybackPositionTicks = (long)(DurationTicks * percentage),
                LastPlayedDate = new DateTimeOffset(2026, 9, 28, 21, 0, 0, TimeSpan.Zero).AddMinutes(-index)
            };
        }
        // The handoff shows these progress bars in details/the wall, but not in its five-item resume shelf.
        _hiddenResume.UnionWith(["1001", "1014"]);
        _configuration = _configuration with { EnableNextEpisodeAutoPlay = true, ResumeRewindSeconds = 0, IntroSkipMode = "ShowButton" };

        void MarkPlayed(string id) => _userData[id] = _userData[id] with
        {
            Played = true, PlaybackPositionTicks = 0, PlayCount = 1,
            LastPlayedDate = new DateTimeOffset(2026, 9, 27, 20, 0, 0, TimeSpan.Zero)
        };
    }
}
