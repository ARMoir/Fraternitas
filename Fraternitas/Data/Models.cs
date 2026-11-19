using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace Fraternitas.Data;

public sealed class Member : IdentityUser
{
    [MaxLength(100)] public string DisplayName { get; set; } = "";
    public int GroupId { get; set; } = 1;
}

public sealed class FraternityGroup
{
    public int Id { get; set; }
    [MaxLength(120)] public string Name { get; set; } = "Men of Advent";
    [MaxLength(500)] public string Description { get; set; } = "";
}

public sealed class Reading
{
    public int Id { get; set; }
    public int Week { get; set; }
    [MaxLength(120)] public string Title { get; set; } = "";
    [MaxLength(100)] public string Scripture { get; set; } = "";
    [MaxLength(500)] public string Summary { get; set; } = "";
}

public sealed class CalendarEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int GroupId { get; set; } = 1;
    [Required, MaxLength(150)] public string Title { get; set; } = "";
    // Group-local wall-clock time, America/New_York; converted for iCalendar export.
    public DateTime StartLocal { get; set; }
    public DateTime EndLocal { get; set; }
    [MaxLength(32)] public string Category { get; set; } = "Meeting";
    [MaxLength(5000)] public string Description { get; set; } = "";
    [MaxLength(100)] public string Scripture { get; set; } = "";
    [MaxLength(1000)] public string MeetingUrl { get; set; } = "";
    public int? ReadingId { get; set; }
    public Reading? Reading { get; set; }
}

public sealed class Discussion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int GroupId { get; set; } = 1;
    public Guid? EventId { get; set; }
    public CalendarEvent? Event { get; set; }
    [Required, MaxLength(150)] public string Title { get; set; } = "";
    [Required, MaxLength(6000)] public string Body { get; set; } = "";
    public string AuthorId { get; set; } = "";
    public Member? Author { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<Reply> Replies { get; set; } = [];
}

public sealed class Reply
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DiscussionId { get; set; }
    public Discussion? Discussion { get; set; }
    [Required, MaxLength(3000)] public string Body { get; set; } = "";
    public string AuthorId { get; set; } = "";
    public Member? Author { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class SharedDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int GroupId { get; set; } = 1;
    public Guid? EventId { get; set; }
    public CalendarEvent? Event { get; set; }
    [MaxLength(180)] public string FileName { get; set; } = "";
    [MaxLength(120)] public string ContentType { get; set; } = "";
    [MaxLength(80)] public string StoredName { get; set; } = "";
    public long Length { get; set; }
    public string UploadedById { get; set; } = "";
    public Member? UploadedBy { get; set; }
    public DateTime UploadedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class Invitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int GroupId { get; set; } = 1;
    [MaxLength(64)] public string TokenSha256 { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
    public DateTime? RedeemedUtc { get; set; }
}

public static class GroupClock
{
    public const string TimeZoneId = "America/New_York";
    public static TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);
    public static DateTime ToUtc(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);
}
