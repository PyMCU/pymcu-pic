using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.PIC;

[TestFixture]
public class Pic18SignednessTests
{
    private const string Header = """
        from pymcu.types import uint8, int8, uint16
        from pymcu.chips.pic18f45k50 import LATD, TRISD, PORTB


        def main():
            TRISD.value = 0
            s: uint8 = PORTB.value
        """;

    private static string Asm(string body) =>
        PymcuCompiler.BuildSourceAsm(Header + "\n" + body, target: "pic18f45k50", frequencyHz: 16_000_000);

    [Test]
    public void SignedRightShift_IsArithmetic()
    {
        var asm = Asm("""
                a: int8 = (0 - 16) - s
                LATD.value = a >> 2
            """);

        asm.Should().NotMatchRegex(@"BCF\s+STATUS,\s*C.*\n\s*RRCF",
            "a signed >> must copy the sign bit into carry, not clear it");
    }

    [Test]
    public void ShiftCountAboveSeven_IsNotMasked()
    {
        var asm = Asm("""
                b: uint16 = 0x1234 + s
                LATD.value = b >> 8
            """);

        asm.Should().MatchRegex(@"MOVF\s+main\.b\+1",
            "a >> 8 on a 16-bit value should read the high byte, not silently become >> 0");
    }

    [Test]
    public void MultiByteAssignment_WritesEveryByte()
    {
        var asm = Asm("""
                b: uint16 = 0x1234 + s
                LATD.value = b >> 8
            """);

        asm.Should().MatchRegex(@"MOVWF\s+main\.b\+1",
            "the high byte of the 16-bit variable must be written, not just read");
    }

    [Test]
    public void SignedComparison_UsesSignedBranch()
    {
        var asm = Asm("""
                d1: int8 = (0 - 5) - s
                d2: int8 = 1 + s
                if d1 < d2:
                    LATD.value = 1
                else:
                    LATD.value = 0
            """);

        Regex.Matches(asm, @"XORLW\s+0x80").Count.Should().BeGreaterThanOrEqualTo(2,
            "a signed comparison biases BOTH operands by 0x80 so the carry test keeps the signed order");
        asm.Should().MatchRegex(@"XORLW\s+0x80\s*\n\s*SUBWF",
            "the biased right-hand side must feed the subtract that sets the carry");
    }

    [Test]
    public void WideComparison_ComparesEveryByte()
    {
        var asm = Asm("""
                g1: uint16 = 0x8000 + s
                g2: uint16 = 5 + s
                if g1 > g2:
                    LATD.value = 1
                else:
                    LATD.value = 0
            """);

        asm.Should().Contain("SUBWFB",
            "a 16-bit comparison must borrow through the high byte");
    }
}
