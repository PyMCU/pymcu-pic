using FluentAssertions;
using NUnit.Framework;
using PicSharp.Peripherals;
using PicSharp.TestKit.Boards;
using PicSharp.TestKit.Extensions;

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

    // NegatedFloatLiteral_Compiles and NegatedFloatLiteral_UnderFloorDivision_Compiles
    // used to assert on `MOVLW 0x40; XORLW 0x80` -- a RUNTIME sign-bit flip the compiler
    // emitted to negate the literal's already-loaded bytes. The shared compiler now
    // constant-folds a literal float negation (-3.7, -7.5) directly to its already-negated
    // IEEE754 bit pattern at compile time (-3.7f = 0xC06CCCCD, loaded as four plain MOVLW
    // bytes -- 0xCD, 0xCC, 0x6C, 0xC0 -- measured on this branch; no XORLW 0x80 anywhere),
    // which is strictly better codegen, not a regression: NegationRewrittenAsSubtraction_
    // StillCompiles below already documented this exact fold for the `0.0 - 3.7` spelling,
    // and these two tests simply had not been updated when the fold grew to cover the
    // literal `-3.7` spelling too.
    //
    // Rewritten as end-to-end value checks instead of assembly-pattern checks, now that
    // SimSession<Pic18F45K50Simulation> (fix/pic-simsession) can run a real PIC18 program:
    // this asserts the COMPUTED VALUE is right, which holds regardless of which of the two
    // equally-correct instruction sequences the compiler picks to get there.
    private static SimSession<Pic18F45K50Simulation> BuildSession(string body) =>
        new(PymcuCompiler.BuildSource(Header + "\n" + body, target: "pic18f45k50", frequencyHz: 16_000_000),
            clockHz: 16_000_000);

    private static void DriveInput(Gpio port, byte value)
    {
        for (int pin = 0; pin < 8; pin++)
            port.SetInput(pin, (value & (1 << pin)) != 0);
    }

    // s = 10 -> f = 10.0. CPython: int(-3.7 + 10.0) = 6; int((-7.5 - 10.0) // 2.0) =
    // int(-17.5 // 2.0) = int(-9.0) = -9, & 0xFF = 247 (floor division rounds toward
    // negative infinity, not toward zero -- -17.5 // 2.0 is -9.0, not -8.0).
    [Test]
    public void NegatedFloatLiteral_MatchesCPython()
    {
        var pic = BuildSession("""
                r: int32 = int32(-3.7 + f)
                LATD.value = uint8(r & 0xFF)
                while True:
                    pass
            """).Reset();
        DriveInput(pic.PortB, 10);
        pic.RunCycles(200_000);
        pic.PortD.Should().HaveValue(6, "int(-3.7 + 10.0) == 6 in CPython");
    }

    [Test]
    public void NegatedFloatLiteral_UnderFloorDivision_MatchesCPython()
    {
        var pic = BuildSession("""
                r: int32 = int32((-7.5 - f) // 2.0)
                LATD.value = uint8(r & 0xFF)
                while True:
                    pass
            """).Reset();
        DriveInput(pic.PortB, 10);
        pic.RunCycles(200_000);
        pic.PortD.Should().HaveValue(247,
            "int((-7.5 - 10.0) // 2.0) == -9 in CPython, & 0xFF == 247 -- floor division " +
            "rounds toward negative infinity");
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
