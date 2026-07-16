using FluentAssertions;
using NUnit.Framework;
using PicSharp.Peripherals;

namespace PyMCU.IntegrationTests.Tests.PIC;

/// <summary>
/// Regression test: AugAssign shift loops use the <c>__tmp</c> counter cell in
/// bank 0, but the shift count is loaded with <c>LoadIntoW(operand)</c> which
/// bank-selects the OPERAND. With a bank-1 operand (an SFR like TRISC at 0x87)
/// the buggy codegen wrote the count into the bank-1 mirror of <c>__tmp</c>
/// while the loop decrement ran on the bank-0 cell (16-bit path: ~256 bogus
/// shifts -> result 0), and the 8-bit path rotated the bank-1 mirror of the
/// target instead of the target itself (result unchanged).
///
/// Note the shift TARGET can never live outside bank 0 today (the bump
/// allocator is bank-0-only and throws on overflow); only the operand can.
///
/// TRISC doubles as the runtime shift count (2 = RC1 input, harmless: PORTC is
/// not part of the test protocol). Results on PORTD, stage on PORTE, acks on
/// PORTA pins (same protocol as MulDivModTests).
/// </summary>
[TestFixture]
public class AugShiftBankingTests
{
    private const string Source = """
        from pymcu.chips.pic16f877a import PORTA, PORTB, PORTD, PORTE, TRISA, TRISB, TRISC, TRISD, TRISE
        from pymcu.types import uint8, uint16

        def main():
            TRISB.value = 0xFF
            TRISA.value = 0xFF
            TRISD.value = 0
            TRISE.value = 0
            TRISC.value = 2
            n: uint8 = PORTB.value & 0x03
            x: uint16 = n * 100
            x <<= TRISC.value
            PORTD.value = x >> 8
            PORTE.value = 1
            while PORTA[5] == 0:
                pass
            PORTD.value = x & 0xFF
            PORTE.value = 2
            while PORTA[4] == 0:
                pass
            y: uint16 = n * 100
            y >>= TRISC.value
            PORTD.value = y & 0xFF
            PORTE.value = 3
            while PORTA[3] == 0:
                pass
            b: uint8 = PORTB.value
            b <<= TRISC.value
            PORTD.value = b
            PORTE.value = 4
            while True:
                pass
        """;

    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildSource(Source));

    private static void Drive(Gpio port, byte value)
    {
        for (int pin = 0; pin < 8; pin++)
            port.SetInput(pin, (value & (1 << pin)) != 0);
    }

    [Test]
    public void AugAssignShift_Bank1Operand_UsesConsistentBanking()
    {
        var pic = _session.Reset();

        // n = 3 -> x = 300; x <<= 2 -> 1200 = 0x04B0; y = 300 >> 2 = 75; b = 3 << 2 = 12.
        Drive(pic.PortB, 3);

        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 1);
        pic.PortD.Pins.Should().Be((byte)(1200 >> 8), "high byte of 300 << 2 (buggy banking shifted ~256 times -> 0)");

        pic.PortA.SetInput(5, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 2);
        pic.PortD.Pins.Should().Be((byte)(1200 & 0xFF), "low byte of 300 << 2");

        pic.PortA.SetInput(4, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 3);
        pic.PortD.Pins.Should().Be((byte)75, "300 >> 2");

        pic.PortA.SetInput(3, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 4);
        pic.PortD.Pins.Should().Be((byte)12, "3 << 2 (buggy banking rotated the bank-1 mirror of b)");
    }
}
