using Xunit;

namespace Segra.Tests;

// xunit runs test classes in parallel, and ModelService's session cache is process-wide state:
// two classes loading and unloading the same game at once would see each other's reference counts.
// Every class that calls ModelService.LoadModel/UnloadModel joins this collection so they run one
// at a time. This serializes those classes only — it does not disable parallelism anywhere else.
[CollectionDefinition(Name)]
public class ModelSessionCollection
{
    public const string Name = "ONNX model session cache";
}
