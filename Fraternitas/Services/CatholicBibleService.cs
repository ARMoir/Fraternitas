using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Fraternitas.Services;

public sealed record CatholicBibleVerse(int Number, string Text);

public sealed record CatholicPassage(
    string Reference,
    IReadOnlyList<CatholicBibleVerse> Verses,
    string? Error = null)
{
    public bool IsAvailable => Error is null && Verses.Count != 0;
}

/// <summary>
/// Fetches only the Douay-Rheims 1899 translation from HelloAO.
/// Before displaying any verse it verifies that this edition's actual books.json
/// includes the 73-book Catholic canon, including all seven deuterocanonical books.
/// This is Scripture for group Lectio Divina, not a daily Mass lectionary.
/// </summary>
public sealed class CatholicBibleService(
    HttpClient client,
    IMemoryCache cache,
    ILogger<CatholicBibleService> logger)
{
    public const string TranslationId = "eng_dra";
    public const string TranslationName = "Douay–Rheims (1899, Catholic)";
    public const string SourceUrl = "https://bible.helloao.org/docs/guide/getting-started.html";

    // This first implementation intentionally limits fetched passages to the
    // group's five Gospel readings. Do not silently switch translations.
    private static readonly Regex LukeReference = new(
        @"^Luke\s+(\d+):(\d+)(?:\s*[-–—]\s*(\d+))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> RequiredDeuterocanon = new(
        ["TOB", "JDT", "WIS", "SIR", "BAR", "1MA", "2MA"],
        StringComparer.OrdinalIgnoreCase);

    public async Task<CatholicPassage> GetPassageAsync(string reference)
    {
        var match = LukeReference.Match(reference.Trim());
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, out int chapter)
            || !int.TryParse(match.Groups[2].Value, out int first)
            || (match.Groups[3].Success && !int.TryParse(match.Groups[3].Value, out _)))
        {
            return new(reference, [],
                "This reference is not yet supported by the Catholic Scripture reader.");
        }

        var last = match.Groups[3].Success
            ? int.Parse(match.Groups[3].Value)
            : first;
        if (chapter is < 1 or > 24 || first < 1 || last < first || last - first > 100)
        {
            return new(reference, [],
                "This reference is not yet supported by the Catholic Scripture reader.");
        }

        try
        {
            await VerifyCatholicCanonAsync();
            var verses = await GetLukeChapterAsync(chapter);
            var selected = verses.Where(v => v.Number >= first && v.Number <= last)
                .OrderBy(v => v.Number).ToList();

            // A partial result must not masquerade as the entire reading.
            if (selected.Select(v => v.Number).Distinct().Count() != last - first + 1)
                throw new InvalidDataException($"Incomplete Catholic passage for {reference}.");

            return new(reference, selected);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or JsonException or InvalidDataException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Catholic Scripture lookup failed for {Reference}", reference);
            return new(reference, [],
                "The Catholic Bible text is temporarily unavailable. The Scripture reference remains above; please try again later.");
        }
    }

    private async Task VerifyCatholicCanonAsync()
    {
        var valid = await cache.GetOrCreateAsync("catholic-bible:eng_dra:canon", async entry =>
        {
            using var response = await client.GetAsync("api/eng_dra/books.json");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;
            var books = root.GetProperty("books");

            // The summary feed reports 66 for eng_dra, but the actual book
            // inventory has 73. Validate the inventory, not summary metadata.
            var ids = books.EnumerateArray()
                .Select(book => book.GetProperty("id").GetString() ?? "")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (books.GetArrayLength() != 73 || !RequiredDeuterocanon.IsSubsetOf(ids))
                throw new InvalidDataException(
                    "HelloAO did not supply the complete 73-book Catholic Douay-Rheims edition.");

            if (root.TryGetProperty("translation", out var translation)
                && translation.TryGetProperty("id", out var id)
                && !string.Equals(id.GetString(), TranslationId, StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected Bible translation identity.");

            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24);
            return true;
        });

        if (valid != true)
            throw new InvalidDataException("The Catholic Bible canon could not be verified.");
    }

    private async Task<List<CatholicBibleVerse>> GetLukeChapterAsync(int chapter)
    {
        var verses = await cache.GetOrCreateAsync($"catholic-bible:eng_dra:LUK:{chapter}",
            async entry =>
            {
                using var response = await client.GetAsync($"api/eng_dra/LUK/{chapter}.simple.json");
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
                        || !number.TryGetInt32(out int verse)
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

        return verses ?? throw new InvalidDataException("Catholic Bible chapter was empty.");
    }
}
