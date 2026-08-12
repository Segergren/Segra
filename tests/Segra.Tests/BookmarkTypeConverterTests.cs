using System;
using System.Text.Json;
using Segra.Backend.Core.Models;
using Xunit;

namespace Segra.Tests;

// BookmarkTypeConverter documents itself as "falling back to Manual for unknown/removed values",
// but it used to reach straight for reader.GetString(), which throws InvalidOperationException on
// any token that is not a string. A single hand-edited or half-written bookmark in a .json metadata
// file therefore failed the deserialization of the whole file, losing every other bookmark and the
// content metadata with it. These tests pin the graceful degradation the doc comment promises.
public class BookmarkTypeConverterTests
{
    private static Bookmark Deserialize(string typeToken) =>
        JsonSerializer.Deserialize<Bookmark>($$"""{"Type":{{typeToken}},"Time":"00:00:10"}""")!;

    [Fact]
    public void KnownName_RoundTrips()
    {
        Assert.Equal(BookmarkType.Kill, Deserialize("\"Kill\"").Type);
    }

    // The converter parses case-insensitively; older files wrote lowercase names.
    [Fact]
    public void KnownName_IsCaseInsensitive()
    {
        Assert.Equal(BookmarkType.Goal, Deserialize("\"goal\"").Type);
    }

    [Fact]
    public void UnknownName_FallsBackToManual()
    {
        Assert.Equal(BookmarkType.Manual, Deserialize("\"RemovedInAnEarlierVersion\"").Type);
    }

    // The pre-fix crash case: a numeric token (e.g. a file written by a serializer that emitted the
    // enum's ordinal) threw out of GetString() instead of falling back.
    [Fact]
    public void NumericToken_FallsBackToManual()
    {
        Assert.Equal(BookmarkType.Manual, Deserialize("3").Type);
    }

    [Fact]
    public void NumericToken_DoesNotFailTheRestOfTheObject()
    {
        var bookmark = Deserialize("3");

        Assert.Equal(BookmarkType.Manual, bookmark.Type);
        Assert.Equal(TimeSpan.FromSeconds(10), bookmark.Time);
    }

    [Fact]
    public void NullToken_FallsBackToManual()
    {
        Assert.Equal(BookmarkType.Manual, Deserialize("null").Type);
    }

    [Fact]
    public void BooleanToken_FallsBackToManual()
    {
        Assert.Equal(BookmarkType.Manual, Deserialize("true").Type);
    }

    // Structured tokens have to be consumed whole, or the serializer throws "read too much or not
    // enough" over the tokens the converter left on the reader.
    [Fact]
    public void ObjectToken_FallsBackToManual()
    {
        Assert.Equal(BookmarkType.Manual, Deserialize("""{"name":"Kill","nested":{"a":[1,2]}}""").Type);
    }

    [Fact]
    public void ArrayToken_FallsBackToManual()
    {
        Assert.Equal(BookmarkType.Manual, Deserialize("""["Kill"]""").Type);
    }
}
