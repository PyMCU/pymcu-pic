using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.PIC;

[TestFixture]
public class Pic18WideMulTests
{
    private const string Source = """
        from pymcu.types import uint8, uint16, uint32
        from pymcu.chips.pic18f45k50 import LATD, TRISD, PORTB


        def main():
            TRISD.value = 0
            s: uint8 = PORTB.value
            a: uint16 = 100 + s
            b: uint16 = a * 3 + 1
            LATD.value = uint8(b & 0xFF)
        """;

    [Test]
    public void ProductFeedingAnotherOperation_Compiles()
    {
        var asm = PymcuCompiler.BuildSourceAsm(Source, target: "pic18f45k50", frequencyHz: 16_000_000);

        asm.Should().MatchRegex(@"MULWF",
            "the 32-bit intermediate the frontend promotes to must still reach the hardware multiplier");
        asm.Should().MatchRegex(@"ADDWFC\s+__mul_r\+3",
            "the carry must reach the top byte of the 32-bit product");
    }

    [Test]
    public void ProductFeedingAnotherOperation_Assembles()
    {
        var lst = PymcuCompiler.BuildSourceListing(Source, target: "pic18f45k50", frequencyHz: 16_000_000);

        lst.Should().NotContain("Error[", "the emitted multiply must assemble cleanly");
    }
}
