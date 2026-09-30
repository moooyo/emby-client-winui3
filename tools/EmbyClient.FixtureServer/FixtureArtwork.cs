using System.Security.Cryptography;

namespace EmbyClient.FixtureServer;

internal sealed record FixtureArtworkSelection(string Primary, string Backdrop, string Thumb, string PrimaryKind = "p");
internal sealed record FixtureImage(byte[] Bytes, string ContentType, string Tag);

internal sealed class FixtureArtwork
{
    private readonly Dictionary<string, FixtureImage> _images = new(StringComparer.Ordinal);

    public FixtureArtwork(string directory, IEnumerable<FixtureArtworkSelection> selections)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var ownedPrefix = root + Path.DirectorySeparatorChar;
        foreach (var selection in selections.Distinct())
        {
            Read(selection.PrimaryKind, selection.Primary);
            Read("b", selection.Backdrop);
            Read("s", selection.Thumb);
        }

        void Read(string kind, string key)
        {
            if (kind is not ("p" or "b" or "s") || key.Length == 0 || !key.All(char.IsAsciiDigit))
                throw new InvalidOperationException("Artwork keys must come from the fixture's fixed numeric mapping.");
            var cacheKey = kind + "/" + key;
            if (_images.ContainsKey(cacheKey)) return;
            var path = Path.GetFullPath(Path.Combine(root, "assets", kind, key + ".jpg"));
            if (!path.StartsWith(ownedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Artwork must remain inside the explicitly selected handoff directory.");
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is < 4 or > 4 * 1024 * 1024)
                throw new InvalidOperationException("A required handoff JPEG is missing or outside the fixture's size limit.");
            var bytes = File.ReadAllBytes(path);
            if (bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[^2] != 0xff || bytes[^1] != 0xd9)
                throw new InvalidOperationException("A required handoff artwork file does not have JPEG markers.");
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];
            _images[cacheKey] = new FixtureImage(bytes, "image/jpeg", "lumen-jpeg-" + hash);
        }
    }

    public FixtureImage Get(FixtureArtworkSelection selection, string imageType) => imageType switch
    {
        "Primary" => _images[selection.PrimaryKind + "/" + selection.Primary],
        "Backdrop" => _images["b/" + selection.Backdrop],
        "Thumb" or "Chapter" => _images["s/" + selection.Thumb],
        _ => throw new ArgumentOutOfRangeException(nameof(imageType))
    };
}
