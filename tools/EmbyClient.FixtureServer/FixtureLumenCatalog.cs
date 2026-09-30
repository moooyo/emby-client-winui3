using System.Globalization;
using EmbyClient.Api;

namespace EmbyClient.FixtureServer;

internal sealed partial class FixtureState
{
    private const string LumenNotice = "Fictional catalog entry. Playback uses an original synthetic color-and-tone test clip, not the pictured film.";
    private static readonly string[] LumenGenres =
    [
        "Action", "Adventure", "Animation", "Comedy", "Crime", "Documentary", "Drama", "Family",
        "Fantasy", "Horror", "Music", "Mystery", "Romance", "Science Fiction", "Thriller", "Western"
    ];
    private static readonly string[] LumenBackdrops = ["515", "249", "525", "683", "227", "541", "797", "666"];
    private static readonly string[] LumenThumbs =
    ["158", "227", "249", "293", "404", "515", "525", "541", "552", "581", "594", "598", "612", "639", "655", "666", "683", "700", "797", "804", "841"];
    private static readonly (string Name, string Type)[] LumenPeople =
    [
        ("Mara Night", "Actor"), ("Owen Mercer", "Actor"), ("Elena Ward", "Actor"), ("Noah Vale", "Actor"),
        ("Iris Lane", "Actor"), ("Theo Quinn", "Actor"), ("Ada Bell", "Actor"), ("Felix Reed", "Actor"),
        ("Julian Night", "Director"), ("Lena Hale", "Director"), ("June Archer", "Writer"), ("Milo Hart", "Producer")
    ];
    private readonly Dictionary<string, FixtureArtworkSelection> _artworkSelections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _collectionMembers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _localTrailers = new(StringComparer.Ordinal);

    private IEnumerable<BaseItemDto> CreateLumenCatalog()
    {
        if (Options.LumenDesignCatalog) return CreateLumenDesignCatalog();

        (string Art, string Name, int Year, string[] Genres, string Synopsis)[] movies =
        [
            ("515", "The Fallen Ground", 2025, ["Science Fiction", "Adventure"], "A vanished aircraft returns to a black-sand shore. An investigator follows its still-ticking clocks into a forbidden valley."),
            ("249", "Sleepless City", 2024, ["Drama", "Mystery"], "A late-night taxi crosses a waking city while two strangers trade the secrets that have kept them awake."),
            ("525", "Beyond the Dunes", 2023, ["Adventure", "Drama"], "A cartographer and a reluctant guide search the desert for a village absent from every map."),
            ("683", "Star Trails", 2026, ["Science Fiction", "Mystery"], "An astronomer finds an impossible line in a long exposure and races the polar night to identify its source."),
            ("227", "Train in the Mist", 2022, ["Mystery", "Thriller"], "The last train of the evening stops at a station that closed decades ago. Every passenger recognizes someone waiting there."),
            ("804", "The Way Home", 2024, ["Drama", "Family"], "A musician returns to a coastal hometown and discovers that the house she inherited contains a second family's history."),
            ("655", "Blue Van", 2023, ["Comedy", "Adventure"], "Three friends set off in an unreliable van to return a misplaced wedding cake before the ceremony begins."),
            ("633", "The Forest Guest", 2022, ["Thriller", "Horror"], "A ranger offers shelter to a traveler whose footprints never appear in the snow outside the cabin."),
            ("778", "Summer Solstice", 2021, ["Romance", "Drama"], "Two childhood friends reunite for the longest day of the year and decide what to leave behind before sunset."),
            ("660", "Fireworks", 2024, ["Romance", "Comedy"], "A quiet festival technician and an ambitious photographer share an unexpected view of a city celebrating change."),
            ("726", "Snowline", 2023, ["Drama", "Adventure"], "A mountain rescue volunteer follows a fading radio signal above the last safe trail."),
            ("375", "The Nameless", 2025, ["Action", "Thriller"], "A courier with no identity must deliver a sealed letter while every camera in the city follows his route."),
            ("331", "The Lone Walker", 2021, ["Crime", "Drama"], "A retired detective retraces one final case through the neighborhood where nobody remembers his name."),
            ("494", "The Ninth Station", 2024, ["Mystery", "Science Fiction"], "A night-shift dispatcher hears tomorrow's emergency calls on an abandoned railway frequency."),
            ("579", "Under Neon", 2023, ["Crime", "Thriller"], "A restaurant cleaner finds a notebook that links the city's brightest night streets to its oldest disappearance."),
            ("247", "Red Rock", 2022, ["Western", "Drama"], "A pair of siblings return to a remote ranch when their father's last letter reveals a disputed boundary."),
            ("545", "A Night in Prague", 2020, ["Romance", "Drama"], "Two travelers miss the same train and spend one night searching for a bookstore that may not exist."),
            ("556", "Old Valley", 2021, ["Horror", "Mystery"], "A restoration crew wakes a valley's forgotten warning bells while rebuilding a ruined chapel."),
            ("447", "Distant Mountains", 2024, ["Drama", "Family"], "A teacher carries a box of unfinished letters across a remote mountain pass to find their intended readers."),
            ("581", "Deep Blue", 2023, ["Documentary", "Adventure"], "A fictional research voyage charts the relationship between ocean currents and a changing coastal community."),
            ("563", "The Long Road", 2022, ["Adventure", "Drama"], "A bus driver and his estranged daughter cross a continent on the route he promised would be his last."),
            ("122", "London Fog", 2019, ["Mystery", "Crime"], "A night watchman notices that one room in an empty hotel is lit at precisely the same minute each evening."),
            ("453", "Echo", 2024, ["Music", "Drama"], "A sound archivist pieces together a lost concert from recordings made by listeners in different parts of the city."),
            ("832", "Her Reading Room", 2025, ["Drama", "Romance"], "A librarian discovers marginal notes that trace a decades-long friendship between two readers who never met."),
            ("838", "A Father's City", 2023, ["Family", "Drama"], "An architect learns to see his hometown through the sketches his father made on daily walks."),
            ("408", "Last Bus", 2024, ["Drama", "Mystery"], "The passengers of a midnight bus discover that each has received an invitation to the same unfamiliar address."),
            ("396", "The Tunnel", 2026, ["Science Fiction", "Thriller"], "Surveyors beneath the city find a tunnel that changes its length every time the lights go out."),
            ("402", "Sunset Boulevard", 2021, ["Romance", "Drama"], "A projectionist and a dancer meet on the last evening of an old neighborhood cinema."),
            ("352", "Nightwalker", 2025, ["Action", "Crime"], "A bike messenger must cross the sleeping city before dawn to clear a friend's name."),
            ("404", "Eye of the Storm", 2024, ["Action", "Adventure"], "Two weather observers remain at an offshore station as an approaching storm cuts every path home."),
            ("786", "Sixty Degrees North", 2023, ["Drama", "Adventure"], "A radio engineer accepts a winter assignment at the northernmost relay station and finds a message waiting there."),
            ("757", "The Journey", 2022, ["Adventure", "Family"], "A child follows an illustrated travel journal to reunite a collection of photographs with their owners."),
            ("345", "Side Streets", 2024, ["Crime", "Mystery"], "A neighborhood reporter follows a missing delivery through the city's overlooked alleyways."),
            ("685", "Above the Clouds", 2024, ["Fantasy", "Animation"], "A young inventor builds a glider to reach a village said to travel with the summer clouds."),
            ("826", "Paper Ferry", 2025, ["Romance", "Drama"], "A ferry captain carries handwritten messages between two riverbank towns during a season of high water."),
            ("443", "Frostline", 2023, ["Adventure", "Documentary"], "A fictional expedition studies the stories, routes, and rituals that connect isolated alpine settlements."),
            ("419", "The Long Goodbye", 2025, ["Family", "Drama"], "An extended family gathers to pack a beloved shop and discovers objects that change their memories of home."),
            ("594", "Night Flight", 2024, ["Mystery", "Thriller"], "A pilot on a quiet overnight route receives a distress call from an aircraft with her own flight number."),
            ("391", "Skyline", 2023, ["Comedy", "Romance"], "Two rival window cleaners compete for a rooftop concert ticket while learning to see their city differently."),
            ("639", "Waterline", 2022, ["Animation", "Family"], "A curious river spirit helps a small town rediscover the waterway running beneath its streets.")
        ];
        var catalog = new List<BaseItemDto>();
        catalog.Add(LumenItem("movies", "Movies", "CollectionFolder", null, "515", "515", "515", landscape: true)
            with { CollectionType = "movies", ChildCount = movies.Length, MovieCount = movies.Length });
        catalog.Add(LumenItem("shows", "Series", "CollectionFolder", null, "797", "797", "797", landscape: true)
            with { CollectionType = "tvshows", ChildCount = 6, SeriesCount = 6 });
        catalog.Add(LumenItem("collections", "Collections", "CollectionFolder", null, "249", "249", "249", landscape: true)
            with { CollectionType = "boxsets", ChildCount = 3 });
        for (var index = 0; index < movies.Length; index++)
        {
            var spec = movies[index];
            var id = (1001 + index).ToString(CultureInfo.InvariantCulture);
            catalog.Add(LumenItem(id, spec.Name, "Movie", "movies", spec.Art) with
            {
                ProductionYear = spec.Year, PremiereDate = new DateTimeOffset(spec.Year, 3 + index % 7, 1 + index % 25, 0, 0, 0, TimeSpan.Zero),
                Overview = spec.Synopsis + " " + LumenNotice, CommunityRating = index == 0 ? 8.1 : Math.Round(6.8 + index % 19 * .1, 1),
                OfficialRating = index % 4 == 0 ? "PG-13" : "PG", Genres = spec.Genres,
                GenreItems = GenrePairs(spec.Genres), People = CastFor(index), Tags = ["Synthetic", index % 3 == 0 ? "Night" : "Discovery"],
                LocalTrailerCount = index is 0 or 1 ? 1 : 0
            });
        }

        (int Id, string Name, string Art, int Year, string[] Genres, string Synopsis)[] shows =
        [
            (2000, "Echoes of the Deep", "541", 2024, ["Science Fiction", "Mystery"], "After an earthquake isolates an undersea station, the crew receives a call from a diver who vanished seven years earlier."),
            (2200, "Rain Night", "797", 2026, ["Crime", "Mystery"], "A traffic investigator uncovers a twenty-year-old case while tracing the witnesses to a storm-night collision."),
            (2400, "Bamboo Wanderer", "666", 2025, ["Action", "Adventure"], "A reclusive traveler leaves a bamboo forest when an urgent letter brings an old promise back into his life."),
            (2600, "Letters from Istanbul", "670", 2023, ["Drama", "Romance"], "An interpreter finds a box of undelivered letters and follows their stories across a city of meeting shores."),
            (2800, "Lost Bearings", "291", 2022, ["Mystery", "Drama"], "A volunteer search team discovers that each disappearance points to the same unfamiliar landmark."),
            (3000, "Fog Forest", "543", 2023, ["Thriller", "Fantasy"], "A survey team enters a forest where the morning fog seems to remember the paths that no longer exist.")
        ];
        string[] episodeNames = ["Below the Tide", "The Fracture", "Echo", "Lost Contact", "The Silent Zone", "The Luminous Sea"];
        string[] episodeArt = ["700", "612", "841", "552", "541", "581"];
        for (var showIndex = 0; showIndex < shows.Length; showIndex++)
        {
            var show = shows[showIndex];
            var seriesId = show.Id.ToString(CultureInfo.InvariantCulture);
            catalog.Add(LumenItem(seriesId, show.Name, "Series", "shows", show.Art) with
            {
                ProductionYear = show.Year, PremiereDate = new DateTimeOffset(show.Year, 2, 1, 0, 0, 0, TimeSpan.Zero),
                Genres = show.Genres, GenreItems = GenrePairs(show.Genres), People = CastFor(showIndex),
                Overview = show.Synopsis + " " + LumenNotice, CommunityRating = 8.6 - showIndex * .2, OfficialRating = "TV-14",
                ChildCount = 2, SeasonCount = 2, RecursiveItemCount = 14, Tags = ["Synthetic", showIndex == 1 ? "Night" : "Discovery"],
                LocalTrailerCount = showIndex == 0 ? 1 : 0
            });
            for (var season = 1; season <= 2; season++)
            {
                var seasonNumber = show.Id == 2000 ? 2100 + (season - 1) * 10 : show.Id + season * 10;
                var seasonId = seasonNumber.ToString(CultureInfo.InvariantCulture);
                catalog.Add(LumenItem(seasonId, "Season " + season, "Season", seriesId, show.Art) with
                {
                    SeriesId = seriesId, SeriesName = show.Name, IndexNumber = season, ChildCount = 6,
                    RecursiveItemCount = 6, ProductionYear = Math.Min(2026, show.Year + season - 1),
                    Genres = show.Genres, GenreItems = GenrePairs(show.Genres)
                });
                for (var episode = 1; episode <= 6; episode++)
                {
                    var id = (seasonNumber + episode).ToString(CultureInfo.InvariantCulture);
                    var artIndex = (episode - 1 + showIndex) % episodeArt.Length;
                    var name = showIndex == 0 ? episodeNames[episode - 1] : episodeNames[artIndex];
                    var year = Math.Min(2026, show.Year + season - 1);
                    catalog.Add(LumenItem(id, name, "Episode", seasonId, episodeArt[artIndex],
                        BackdropFor(show.Art), episodeArt[artIndex], landscape: true) with
                    {
                        SeriesId = seriesId, SeriesName = show.Name, SeasonId = seasonId, IndexNumber = episode, ParentIndexNumber = season,
                        ProductionYear = year, PremiereDate = new DateTimeOffset(year, 3, 1 + (episode - 1) * 5, 0, 0, 0, TimeSpan.Zero),
                        Overview = $"The team follows a new clue in {show.Name}; a difficult decision changes the course of their search. " + LumenNotice,
                        Genres = show.Genres, GenreItems = GenrePairs(show.Genres), People = CastFor(showIndex),
                        CommunityRating = 7.8 + episode * .1, OfficialRating = "TV-14", Tags = ["Synthetic"]
                    });
                }
            }
        }

        AddCollection("collection-night", "The Night Trilogy", ["1002", "1015", "1029"], "249");
        AddCollection("collection-frontier", "Beyond the Horizon", ["1001", "1003", "1004", "1011"], "515");
        AddCollection("collection-home", "Ways Back Home", ["1006", "1025", "1032", "1037"], "804");
        foreach (var parentId in new[] { "1001", "1002", "2000" })
        {
            var parent = catalog.Single(item => item.Id == parentId);
            var id = "trailer-" + parentId;
            var selection = _artworkSelections[parentId];
            catalog.Add(LumenItem(id, parent.Name + " - Synthetic Trailer", "Trailer", parentId,
                selection.Thumb, selection.Backdrop, selection.Thumb, landscape: true) with
            {
                Overview = "A local trailer fixture for the fictional catalog entry. " + LumenNotice,
                ProductionYear = parent.ProductionYear, Genres = parent.Genres, GenreItems = parent.GenreItems
            });
            _localTrailers[parentId] = [id];
        }
        for (var index = 0; index < LumenPeople.Length; index++)
        {
            var id = PersonId(index);
            var works = catalog.Where(item => item.Type is "Movie" or "Series" && item.People?.Any(person => person.Id == id) == true).ToArray();
            catalog.Add(new BaseItemDto
            {
                Id = id, Name = LumenPeople[index].Name, SortName = LumenPeople[index].Name, Type = "Person", IsFolder = false,
                Overview = $"A fictional {LumenPeople[index].Type.ToLowerInvariant()} in the synthetic Lumen library.",
                MovieCount = works.Count(item => item.Type == "Movie"), SeriesCount = works.Count(item => item.Type == "Series"),
                PrimaryImageAspectRatio = 1, CanEditItems = false
            });
        }
        for (var index = 0; index < LumenGenres.Length; index++)
        {
            var genre = LumenGenres[index];
            var works = catalog.Where(item => item.Type is "Movie" or "Series" && item.Genres?.Contains(genre, StringComparer.OrdinalIgnoreCase) == true).ToArray();
            var art = works.Length > 0 ? _artworkSelections[works[0].Id!].Primary : "515";
            catalog.Add(LumenItem(GenreId(index), genre, "Genre", null, art) with
            {
                Overview = $"Fictional {genre.ToLowerInvariant()} titles in the synthetic Lumen library.",
                MovieCount = works.Count(item => item.Type == "Movie"), SeriesCount = works.Count(item => item.Type == "Series"), CanEditItems = false
            });
        }
        return catalog;

        void AddCollection(string id, string name, string[] members, string art)
        {
            _collectionMembers[id] = members.ToHashSet(StringComparer.Ordinal);
            catalog.Add(LumenItem(id, name, "BoxSet", "collections", art) with
            {
                ChildCount = members.Length, MovieCount = members.Length, Tags = ["Synthetic", "Night"],
                Overview = "A curated collection of fictional films in the synthetic Lumen library."
            });
        }
    }

    private BaseItemDto LumenItem(string id, string name, string type, string? parent, string art,
        string? backdrop = null, string? thumb = null, bool landscape = false)
    {
        var selection = new FixtureArtworkSelection(art, backdrop ?? BackdropFor(art), thumb ?? ThumbFor(art), landscape ? "s" : "p");
        _artworkSelections[id] = selection;
        var playable = type is "Movie" or "Episode" or "Trailer";
        var sources = playable ? Sources(id) : null;
        return new BaseItemDto
        {
            Id = id, Name = name, OriginalTitle = name, SortName = name, Type = type, ParentId = parent,
            IsFolder = type is "CollectionFolder" or "Series" or "Season" or "BoxSet" or "Genre",
            Overview = LumenNotice, LocationType = "FileSystem", CanEditItems = type is "Movie" or "Series" or "Episode" or "BoxSet",
            ImageTags = new Dictionary<string, string> { ["Primary"] = "lumen-primary-" + art + "-v1", ["Thumb"] = "lumen-thumb-" + selection.Thumb + "-v1" },
            BackdropImageTags = ["lumen-backdrop-" + selection.Backdrop + "-v1"], PrimaryImageAspectRatio = landscape ? 16.0 / 9 : 2.0 / 3,
            MediaType = playable ? "Video" : null, RunTimeTicks = playable ? DurationTicks : null,
            MediaSources = sources, MediaStreams = sources?.FirstOrDefault()?.MediaStreams,
            Chapters = playable ? LumenChapters() : null, RemoteTrailers = []
        };
    }

    private MediaSourceInfo[] Sources(string itemId) => Options.LumenCatalog && itemId == "1001"
        ? [Source(itemId, name: $"H.264 AAC {Options.Media.Height}p - Primary synthetic fixture"),
           Source(itemId, sourceId: "synthetic-mp4-alternate", name: $"H.264 AAC {Options.Media.Height}p - Duplicate fixture alias")]
        : [Source(itemId)];

    private ChapterInfo[] LumenChapters() =>
    [
        Chapter("Opening", 0, "Chapter", 0), Chapter("Intro starts", DurationTicks / 20, "IntroStart", 1),
        Chapter("Intro ends", DurationTicks / 6, "IntroEnd", 2), Chapter("Signal", DurationTicks / 3, "Chapter", 3),
        Chapter("Return", DurationTicks * 2 / 3, "Chapter", 4), Chapter("Credits", DurationTicks * 9 / 10, "CreditsStart", 5)
    ];

    private ChapterInfo Chapter(string name, long ticks, string marker, int index) => new()
    {
        Name = name, StartPositionTicks = ticks, MarkerType = marker, ChapterIndex = index, ImageTag = "lumen-chapter-v1"
    };

    private static string BackdropFor(string art) => LumenBackdrops.Contains(art, StringComparer.Ordinal) ? art
        : LumenBackdrops[int.Parse(art, CultureInfo.InvariantCulture) % LumenBackdrops.Length];
    private static string ThumbFor(string art) => LumenThumbs.Contains(art, StringComparer.Ordinal) ? art
        : LumenThumbs[int.Parse(art, CultureInfo.InvariantCulture) % LumenThumbs.Length];
    private static string PersonId(int index) => $"person-{index + 1:D2}";
    private static string GenreId(int index) => (50001 + index).ToString(CultureInfo.InvariantCulture);
    private static NameLongIdPair[] GenrePairs(string[] genres) => genres.Select(genre => new NameLongIdPair
    {
        Name = genre, Id = 50001 + Array.IndexOf(LumenGenres, genre)
    }).ToArray();
    private static PersonInfo[] CastFor(int offset) => Enumerable.Range(0, 6).Select(index => (offset + index) % 8)
        .Concat([8 + offset % 2, 10, 11]).Select(index => new PersonInfo
        {
            Id = PersonId(index), Name = LumenPeople[index].Name, Type = LumenPeople[index].Type,
            Role = LumenPeople[index].Type == "Actor" ? new[] { "Dr. Rowan", "Captain Hale", "Alex", "The Archivist", "Mira", "The Guide", "Ellis", "The Witness" }[index] : null
        }).ToArray();

    private void SeedLumenUserData()
    {
        if (Options.LumenDesignCatalog)
        {
            SeedLumenDesignUserData();
            return;
        }

        foreach (var id in new[] { "1004", "1014", "1020", "1032", "2000", "2200", "collection-night" })
            _userData[id] = _userData[id] with { IsFavorite = true };
        foreach (var id in new[] { "1002", "1007", "1013", "1022", "1025", "2101", "2102", "2103", "2104", "2105", "2106", "2111", "2112", "2113", "2114", "2211" })
            _userData[id] = _userData[id] with { Played = true, PlaybackPositionTicks = 0, PlayCount = 1, LastPlayedDate = new DateTimeOffset(2026, 9, 27, 20, 0, 0, TimeSpan.Zero) };
        foreach (var (id, percentage) in new[] { ("1001", .35), ("1006", .38), ("1008", .14), ("2115", .52), ("2212", .30) })
            _userData[id] = _userData[id] with { PlaybackPositionTicks = (long)(DurationTicks * percentage), LastPlayedDate = new DateTimeOffset(2026, 9, 28, 21, 0, 0, TimeSpan.Zero) };
        _configuration = _configuration with { EnableNextEpisodeAutoPlay = true, ResumeRewindSeconds = 0, IntroSkipMode = "ShowButton" };
    }
}
