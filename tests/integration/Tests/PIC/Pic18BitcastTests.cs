using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.PIC;

[TestFixture]
public class Pic18BitcastTests
{
    private const string Source = """
        from pymcu.types import uint8, uint32
        from pymcu.chips.pic18f45k50 import LATD, TRISD, PORTB


        def main():
            TRISD.value = 0
            s: uint8 = PORTB.value
            x: float = 1.0 + float(s)
            u: uint32 = bitcast(uint32, x)
            LATD.value = uint8(u >> 24)
        """;

    private static string Asm() =>
        PymcuCompiler.BuildSourceAsm(Source, target: "pic18f45k50", frequencyHz: 16_000_000);

    [Test]
    public void BitcastOfAFloat_DoesNotCallTheConversionRoutine()
    {
        Asm().Should().NotMatchRegex(@"CALL\s+__fp_fix",
            "bitcast reinterprets the four bytes; truncating through __fp_fix turns 1.0 into 1");
    }

    [Test]
    public void BitcastOfAFloat_MovesAllFourBytes()
    {
        var asm = Asm();

        asm.Should().MatchRegex(@"MOVWF\s+main\.u\b");
        asm.Should().MatchRegex(@"MOVWF\s+main\.u\+3");
    }

    [Test]
    public void BitcastOfAFloat_KeepsOnlyTheArithmeticRoutines()
    {
        var asm = Asm();

        asm.Should().MatchRegex(@"CALL\s+__fp_add", "the addition still goes through soft-float");
        asm.Should().MatchRegex(@"CALL\s+__fp_float", "float(s) still goes through soft-float");
    }
}
