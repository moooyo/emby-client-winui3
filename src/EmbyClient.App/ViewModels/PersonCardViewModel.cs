using EmbyClient.Api;

namespace EmbyClient.App.ViewModels;

public sealed partial class PersonCardViewModel
{
    private PersonCardViewModel(PersonInfo person, string role)
    {
        Name = person.Name!;
        Id = person.Id ?? string.Empty;
        Role = role;
        Media = new MediaCardViewModel(new BaseItemDto
        {
            Id = person.Id,
            Name = person.Name,
            Type = "Person",
            ImageTags = string.IsNullOrWhiteSpace(person.PrimaryImageTag)
                ? null : new() { ["Primary"] = person.PrimaryImageTag }
        }, role);
    }

    public string Id { get; }
    public string Name { get; }
    public string Role { get; }
    public MediaCardViewModel Media { get; }
    public bool CanOpen => !string.IsNullOrWhiteSpace(Id);
    public string AutomationName => $"{Name}, {Role}" + (CanOpen ? ". View person and library works." : ". No profile is available.");

    public static IReadOnlyList<PersonCardViewModel> FromItem(BaseItemDto item) =>
        (item.People ?? [])
            .Where(person => !string.IsNullOrWhiteSpace(person.Name))
            .OrderBy(person => person.Type is "Actor" or "GuestStar" ? 0 : 1)
            .GroupBy(person => string.IsNullOrWhiteSpace(person.Id) ? $"name:{person.Name}" : $"id:{person.Id}", StringComparer.Ordinal)
            .Select(group => new PersonCardViewModel(group.First(), string.Join(" · ", group.Select(RoleLabel).Where(role => role.Length > 0).Distinct(StringComparer.Ordinal))))
            .ToArray();

    private static string RoleLabel(PersonInfo person) => !string.IsNullOrWhiteSpace(person.Role)
        ? person.Role : person.Type switch
        {
            "GuestStar" => "Guest star",
            "Actor" => "Actor",
            null or "" => "Cast and crew",
            _ => person.Type
        };
}
