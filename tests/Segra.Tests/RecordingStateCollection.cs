using Xunit;

namespace Segra.Tests;

// A bookmark is only observable through AppState.Instance.Recording, which is process-wide: two
// test classes swapping that recording in parallel would each count the other's bookmarks. Every
// class that installs a recording to watch what a detection cycle writes joins this collection so
// they run one at a time. This serializes those classes only — it does not disable parallelism
// anywhere else.
[CollectionDefinition(Name)]
public class RecordingStateCollection
{
    public const string Name = "AppState recording";
}
