using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.PIC;

[TestFixture]
public class Pic18FloatNegationTests
{
    private const string Header = """
        from pymcu.types import uint8, int32
        from pymcu.chips.pic18f45k50 import LATD, TRISD, PORTB


        def main():
            TRISD.value = 0
            s: uint8 = PORTB.value
            f: float = float(s)
        """;

    private static string Asm(string body) =>
        PymcuCompiler.BuildSourceAsm(Header + "\n" + body, target: "pic18f45k50", frequencyHz: 16_000_000);

    [Test]
    public void NegatedFloatLiteral_Compiles()
    {
        var asm = Asm("""
                r: int32 = int32(-3.7 + f)
                LATD.value = uint8(r & 0xFF)
            """);

        // 3.7f == 0x406CCCCD, so the operand must be built from those bytes with bit 31 flipped
        asm.Should().MatchRegex(@"MOVLW\s+0x40\s*\n\s*XORLW\s+0x80");
        asm.Should().MatchRegex(@"MOVLW\s+0xCD");
        asm.Should().MatchRegex(@"CALL\s+__fp_add");
    }

    [Test]
    public void NegatedFloatLiteral_UnderFloorDivision_Compiles()
    {
        var asm = Asm("""
                r: int32 = int32((-7.5 - f) // 2.0)
                LATD.value = uint8(r & 0xFF)
            """);

        asm.Should().MatchRegex(@"XORLW\s+0x80");
        asm.Should().MatchRegex(@"CALL\s+__fp_floor");
    }

    [Test]
    public void NegationRewrittenAsSubtraction_StillCompiles()
    {
        var asm = Asm("""
                r: int32 = int32(0.0 - 3.7 + f)
                LATD.value = uint8(r & 0xFF)
            """);

        asm.Should().MatchRegex(@"CALL\s+__fp_add",
            "the workaround form folds to a negative literal and adds; it must keep working");
    }
}
