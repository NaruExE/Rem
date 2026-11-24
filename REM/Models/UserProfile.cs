namespace REM.Models;

public sealed class UserProfile
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string AvatarColor { get; set; } = "#7AA2F7";
    public string? AvatarPath { get; set; }
    public string? PasswordSalt { get; set; }
    public string? PasswordHash { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool HasPassword =>
        !string.IsNullOrEmpty(PasswordHash) && !string.IsNullOrEmpty(PasswordSalt);

    public string Initials
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                return "?";
            }

            var parts = Name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                return parts[0][..1].ToUpperInvariant();
            }

            return string.Concat(
                parts[0][..1].ToUpperInvariant(),
                parts[1][..1].ToUpperInvariant());
        }
    }
}
