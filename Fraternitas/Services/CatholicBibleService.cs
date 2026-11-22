using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Fraternitas.Services;

public sealed record CatholicBibleVerse(int Number, string Text);

public sealed record CatholicBibleBook(
    string Id, string Name, int FirstChapter, int LastChapter, string ShortName = "");

public sealed record CatholicPassage(
    string Reference,
    IReadOnlyList<CatholicBibleVerse> Verses,
    string? Error = null)
{
    public bool IsAvailable => Error is null && Verses.Count != 0;
}

/// <summary>
/// Reads the Douay-Rheims (1899) translation from HelloAO. The Bible browser
/// and all Scripture pins use the same verified 73-book Catholic canon.
/// Never substitute an unverified translation when the upstream API fails.
/// </summary>
public sealed class CatholicBibleService(
    HttpClient client,
    IMemoryCache cache,
    ILogger<CatholicBibleService> logger)
{
    public const string TranslationId = "eng_dra";
    public const string TranslationName = "Douay–Rheims (1899, Catholic)";
    public const string SourceUrl = "https://bible.helloao.org/docs/guide/getting-started.html";

    private static readonly Regex ReferencePattern = new(
        @"^(?<book>.+?)\s+(?<chapter>\d+)\s*:\s*(?<first>\d+)(?:\s*[-–—]\s*(?<last>\d+))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> RequiredDeuterocanon = new(
        ["TOB", "JDT", "WIS", "SIR", "BAR", "1MA", "2MA"],
        StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<CatholicBibleBook>> GetBooksAsync()
    {
        var books = await cache.GetOrCreateAsync("catholic-bible:eng_dra:books:v2", async entry =>
        {
            using var response = await client.GetAsync("api/eng_dra/books.json");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;
            if (root.TryGetProperty("translation", out var translation)
                && translation.TryGetProperty("id", out var id)
                && id.GetString() != TranslationId)
                throw new InvalidDataException("Unexpected Bible translation identity.");

            var inventory = root.GetProperty("books");
            var ids = inventory.EnumerateArray()
                .Select(item => item.GetProperty("id").GetString() ?? "")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (inventory.GetArrayLength() != 73 || ids.Count != 73
                || !RequiredDeuterocanon.IsSubsetOf(ids))
                throw new InvalidDataException(
                    "HelloAO did not supply the complete 73-book Catholic Douay-Rheims edition.");

            var parsed = new List<CatholicBibleBook>();
            foreach (var item in inventory.EnumerateArray())
            {
                var bookId = item.GetProperty("id").GetString() ?? "";
                var name = item.GetProperty("commonName").GetString()
                    ?? item.GetProperty("name").GetString() ?? "";
                var first = item.GetProperty("firstChapterNumber").GetInt32();
                var last = item.GetProperty("lastChapterNumber").GetInt32();
                var shortName = item.TryGetProperty("shortName", out var abbreviated)
                    ? abbreviated.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(bookId) || string.IsNullOrWhiteSpace(name)
                    || first < 1 || last < first)
                    throw new InvalidDataException("The Catholic book inventory was incomplete.");

                parsed.Add(new CatholicBibleBook(bookId, name, first, last, shortName));
            }

            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24);
            return parsed;
        });
        return books ?? throw new InvalidDataException("The Catholic book list was unavailable.");
    }

    /// <summary>Display the entire chapter, including chapters longer than 100 verses.</summary>
    public async Task<CatholicPassage> GetChapterAsync(string bookId, int chapter)
    {
        try
        {
            var books = await GetBooksAsync();
            var book = books.FirstOrDefault(b =>
                string.Equals(b.Id, bookId, StringComparison.OrdinalIgnoreCase));
            if (book is null || chapter < book.FirstChapter || chapter > book.LastChapter)
                return new($"{bookId} {chapter}", [], "Select a valid Catholic Bible book and chapter.");

            var verses = await GetChapterVersesAsync(book.Id, chapter);
            return new($"{book.Name} {chapter}:1-{verses.Max(v => v.Number)}", verses);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or JsonException or InvalidDataException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Catholic Scripture chapter lookup failed for {Book} {Chapter}",
                bookId, chapter);
            return new($"{bookId} {chapter}", [],
                "The Catholic Bible text is temporarily unavailable. Please try again.");
        }
    }

    public async Task<CatholicPassage> GetPassageAsync(string reference)
    {
        var trimmed = (reference ?? "").Trim();
        var match = trimmed.Length <= 100 ? ReferencePattern.Match(trimmed) : Match.Empty;
        if (!match.Success || !int.TryParse(match.Groups["chapter"].Value, out var chapter)
            || !int.TryParse(match.Groups["first"].Value, out var first)
            || (match.Groups["last"].Success
                && !int.TryParse(match.Groups["last"].Value, out _)))
            return new(trimmed, [], "Enter a valid book, chapter and verse, e.g. John 3:16.");

        var last = match.Groups["last"].Success
            ? int.Parse(match.Groups["last"].Value) : first;
        if (first < 1 || last < first || last - first > 100)
            return new(trimmed, [], "Select a valid verse range of no more than 101 verses.");

        try
        {
            var books = await GetBooksAsync();
            var bookName = match.Groups["book"].Value.Trim();
            var book = books.FirstOrDefault(b =>
                string.Equals(bookName, b.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(bookName, b.Id, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(b.ShortName)
                    && string.Equals(bookName, b.ShortName, StringComparison.OrdinalIgnoreCase)));
            if (book is null || chapter < book.FirstChapter || chapter > book.LastChapter)
                return new(trimmed, [], "This book or chapter is not available in the Catholic Bible.");

            var verses = await GetChapterVersesAsync(book.Id, chapter);
            var selected = verses.Where(v => v.Number >= first && v.Number <= last)
                .OrderBy(v => v.Number).ToList();
            if (selected.Select(v => v.Number).Distinct().Count() != last - first + 1)
                throw new InvalidDataException($"Incomplete Catholic passage for {trimmed}.");

            return new(trimmed, selected);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or JsonException or InvalidDataException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Catholic Scripture lookup failed for {Reference}", trimmed);
            return new(trimmed, [],
                "The Catholic Bible text is temporarily unavailable. Please try again later.");
        }
    }

    private async Task<List<CatholicBibleVerse>> GetChapterVersesAsync(string bookId, int chapter)
    {
        var verses = await cache.GetOrCreateAsync(
            $"catholic-bible:eng_dra:{bookId}:{chapter}", async entry =>
        {
            using var response = await client.GetAsync(
                $"api/eng_dra/{Uri.EscapeDataString(bookId)}/{chapter}.simple.json");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var blocks = document.RootElement.GetProperty("chapter").GetProperty("content");

            var result = new List<CatholicBibleVerse>();
            foreach (var block in blocks.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var type)
                    || type.GetString() != "verse"
                    || !block.TryGetProperty("number", out var number)
                    || !number.TryGetInt32(out var verse)
                    || !block.TryGetProperty("text", out var text))
                    continue;
                var value = text.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    result.Add(new CatholicBibleVerse(verse, value));
            }
            if (result.Count == 0)
                throw new InvalidDataException("Bible chapter contained no readable verses.");

            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24);
            return result;
        });
        return verses ?? throw new InvalidDataException("The Catholic Bible chapter was empty.");
    }
}
