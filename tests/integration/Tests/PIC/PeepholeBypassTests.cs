using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.PIC;

[TestFixture]
public class PeepholeBypassTests
{
    private const string Program = """
        from pymcu.types import uint8
        from pymcu.chips.pic18f45k50 import LATD, TRISD, PORTB


        def main():
            TRISD.value = 0
            x: uint8 = PORTB.value
            x = x
            LATD.value = x
        """;

    private static string Asm(bool noPeephole) =>
        PymcuCompiler.BuildSourceAsm(Program, target: "pic18f45k50", frequencyHz: 16_000_000,
            noPeephole: noPeephole);

    [Test]
    public void NoPeephole_ReachesTheBackendAndBypassesTheRewrites()
    {
        var optimized = Asm(noPeephole: false);
        var raw = Asm(noPeephole: true);

        optimized.Should().NotContain("MOVFF\tx, x");
        raw.Should().Contain("MOVFF\tmain.x, main.x",
            "PYMCU_NO_PEEPHOLE=1 must travel from the environment through the driver into " +
            "the backend process; if it does not, every bisection using the switch is vacuous");
    }

    [Test]
    public void NoPeephole_StillAssembles()
    {
        var lst = PymcuCompiler.BuildSourceListing(Program, target: "pic18f45k50",
            frequencyHz: 16_000_000, noPeephole: true);

        lst.Should().NotContain("Error[");
    }

    private const string Pic14Program = """
        from pymcu.types import uint8
        from pymcu.chips.pic16f877a import PORTB, TRISB, PORTA


        def main():
            TRISB.value = 0x00
            x: uint8 = PORTA.value
            x = x
            PORTB.value = x
        """;

    [Test]
    public void NoPeephole_ReachesThePic14BackendToo()
    {
        var optimized = PymcuCompiler.BuildSourceAsm(Pic14Program, target: "pic16f877a",
            frequencyHz: 20_000_000);
        var raw = PymcuCompiler.BuildSourceAsm(Pic14Program, target: "pic16f877a",
            frequencyHz: 20_000_000, noPeephole: true);

        raw.Length.Should().BeGreaterThan(optimized.Length,
            "the PIC14 peephole does real work (bit-op coalescing, dead labels, bank " +
            "tracking), so bypassing it must leave a visibly larger image");
    }
}
