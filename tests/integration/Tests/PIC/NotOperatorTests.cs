using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.PIC;

/// <summary>
/// `not` on a NON-boolean operand (oracle probe 296_not_operator.py): PIC12,
/// PIC14 and PIC18 codegen for UnaryOp.Not all materialized the result from
/// the operand's LOW BYTE alone (PIC18: a bitwise complement, COMF; PIC14/
/// PIC12: XORLW 1; ANDLW 1, a low-bit flip). Both answer the right question
/// only when the operand is ALREADY strictly 0 or 1 -- popcount(6) returns 2,
/// and `not 2` must be False. COMF(2) = 253 (truthy) and (2 XOR 1) AND 1 = 1
/// (truthy): both backends printed True where CPython prints False.
///
/// v = 6: popcount(6) = 2 (0b110 has two set bits), v - 6 = 0, v &amp; 1 = 0.
/// CPython: not 2 -> False, not 0 -> True, not 0 -> True.
///
/// PIC16F877A: end-to-end through the real pymcu -> pymcuc-pic -> gpasm ->
/// emulator pipeline (SimSession/RunUntilSerial), the authoritative "matches
/// CPython" proof. PIC18F45K50: an ASSEMBLY-level check instead -- no
/// SimSession-based UART test exists anywhere in this suite for this chip
/// today (every PIC18 UART test, including the pre-existing
/// Pic18UartFacadeTests, asserts on generated assembly text, never runs the
/// emulator), and a program as small as `u.write(v)` for a plain uint8
/// variable already produces no serial output under SimSession here --
/// confirmed unrelated to this fix and out of its scope.
/// </summary>
[TestFixture]
public class NotOperatorTests
{
    private const string Source = """
        from pymcu.types import uint8


        def popcount(n: uint8) -> uint8:
            c: uint8 = 0
            while n != 0:
                c = c + (n & 1)
                n = n >> 1
            return c


        def main():
            v: uint8 = 6
            print(not popcount(v))
            print(not (v - 6))
            print(not (v & 1))
            while True:
                pass
        """;

    private const string Expected = "False\nTrue\nTrue\n";

    [Test]
    public void NotOfANonBooleanValue_MatchesCPython_OnPic16F877A()
    {
        var pic = new SimSession(PymcuCompiler.BuildSource(Source)).Reset();
        pic.RunUntilSerial(pic.Serial, Expected);
        pic.Serial.Text.Should().Be(Expected,
            "`not popcount(v)` tests the WHOLE operand for zero, not its low bit");
    }

    [Test]
    public void NotOfANonBooleanValue_MatchesCPython_OnPic16F877A_NoPeephole()
    {
        // Pins the codegen itself apart from the peephole pass: this must already
        // be correct with PYMCU_NO_PEEPHOLE=1, and stay correct once the peephole
        // runs (the sibling test above) -- the peephole bug this fix also closes
        // (ANDLW 0xFF/ADDLW 0/XORLW 0 dropped unconditionally, even when nothing
        // before them re-set the same flags) only ever hid a codegen bug that
        // would otherwise still be there.
        var pic = new SimSession(PymcuCompiler.BuildSource(Source, noPeephole: true)).Reset();
        pic.RunUntilSerial(pic.Serial, Expected);
        pic.Serial.Text.Should().Be(Expected);
    }

    private static string Pic18Asm(string source) =>
        PymcuCompiler.BuildSourceAsm(source, target: "pic18f45k50", frequencyHz: 16_000_000);

    [Test]
    public void NotOfANonBooleanValue_TestsTheWholeValue_OnPic18F45K50()
    {
        var asm = Pic18Asm("""
            from pymcu.hal.pic18.uart import UART
            from pymcu.types import uint8


            def popcount(n: uint8) -> uint8:
                c: uint8 = 0
                while n != 0:
                    c = c + (n & 1)
                    n = n >> 1
                return c


            def main():
                u = UART(9600)
                v: uint8 = 6
                r: uint8 = not popcount(v)
                u.write(r)
            """);

        // The old bug (COMF WREG, a bitwise complement) must be gone from the
        // `not` materialization: the only COMF left in the file, if any, would
        // belong to an unrelated BitNot (~x), never to this `not`.
        Regex.Matches(asm, @"COMF\s+WREG").Count.Should().Be(0,
            "`not` must not answer with a bitwise complement of the operand");

        // The correct pattern: default to False, then correct to True only when
        // the operand's own Z flag (ANDLW 0xFF right after loading it) says the
        // whole operand was zero.
        asm.Should().MatchRegex(@"ANDLW\s+0xFF\s*\n\s*MOVLW\s+0x00\s*\n\s*BTFSC\s+STATUS,\s*Z,\s*ACCESS\s*\n\s*MOVLW\s+0x01",
            "`not` must test the operand against zero, not flip its low bit or complement its bits");
    }
}
