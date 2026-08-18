using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.PIC;

[TestFixture]
public class Pic18RuntimeDivisionTests
{
    private const string Source = """
        from pymcu.types import uint8
        from pymcu.chips.pic18f45k50 import LATD, TRISD, PORTB


        def mix(a: uint8, b: uint8) -> uint8:
            return a // b


        def main():
            TRISD.value = 0
            s: uint8 = PORTB.value
            LATD.value = mix(s, 3 + (s & 1))
        """;

    [Test]
    public void FloorDivisionInAHelper_CompilesAndParksInsteadOfBranchingNowhere()
    {
        var asm = PymcuCompiler.BuildSourceAsm(Source, target: "pic18f45k50", frequencyHz: 16_000_000);

        asm.Should().NotMatchRegex(@"BNZ\s+__pymcu_unhandled_exn",
            "that label has no PIC definition, so gpasm would fail with Error[113]");
        asm.Should().MatchRegex(@"BNZ\s+__err_trap");
        asm.Should().Contain("__err_trap:");
    }
}
