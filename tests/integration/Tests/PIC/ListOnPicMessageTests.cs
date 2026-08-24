using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.PIC;

/// <summary>
/// `xs: list[uint8]` on a PIC (PyMCU#45).
///
/// A growable list is heap-allocated and only the AVR backend has the collector. The
/// rejection used to come from a much later phase and answered in terms the program does
/// not contain -- "GC_REF / gc_alloc is only supported on AVR targets ... Use @value classes
/// or primitive types" -- with no variable, no line, and no way to tell whether the reader
/// should look for another spelling.
///
/// The fixed array it now points at is asserted to build on the same target, because advice
/// that does not compile is worse than no advice.
/// </summary>
[TestFixture]
public class ListOnPicMessageTests
{
    private const string WithList =
        "from pymcu.types import uint8\n" +
        "\n" +
        "\n" +
        "def main() -> None:\n" +
        "    xs: list[uint8] = [1, 2, 3]\n" +
        "    y: uint8 = xs[1]\n";

    private const string WithFixedArray =
        "from pymcu.types import uint8\n" +
        "\n" +
        "\n" +
        "def main() -> None:\n" +
        "    xs: uint8[3] = [1, 2, 3]\n" +
        "    y: uint8 = xs[1]\n";

    [Test]
    public void ListOnPic_NamesTheVariableAndTheFixedArray()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PymcuCompiler.BuildSource(WithList, target: "pic18f45k50", frequencyHz: 16_000_000));

        ex!.Message.Should().Contain("'xs: list[uint8]'");
        ex.Message.Should().Contain("xs: uint8[3]", "the message must show the spelling that works");
        ex.Message.Should().NotContain("GC_REF", "the reader's program contains no such thing");
    }

    [Test]
    public void TheFixedArrayItSuggests_Builds()
    {
        PymcuCompiler.BuildSource(WithFixedArray, target: "pic18f45k50", frequencyHz: 16_000_000)
            .Should().NotBeEmpty();
    }
}
