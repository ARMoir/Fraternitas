using System.ComponentModel.DataAnnotations;

namespace Fraternitas.Data;

// Requests are not Identity users. They cannot sign in or receive roles.
public sealed class AccountRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(100)] public string DisplayName { get; set; } = "";
    [MaxLength(256)] public string Email { get; set; } = "";
    [MaxLength(256)] public string NormalizedEmail { get; set; } = "";
    [MaxLength(500)] public string Note { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(64)] public string? TokenSha256 { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public bool GrantAdmin { get; set; }
}
