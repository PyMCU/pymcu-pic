using PicSharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;
using PicSharp.Peripherals;

namespace PyMCU.IntegrationTests.Tests.PIC;

/// <summary>
/// Regression test: 16-bit AugAssign Add/Sub with an 8-bit operand must
/// zero-extend the operand instead of reading <c>operand+1</c> (the next
/// variable's RAM slot). Here <c>y</c> is a uint8 whose adjacent cell is the
/// LOW byte of <c>x</c> itself, so the buggy codegen adds/subtracts garbage
/// into the high byte: x += y produced hi = 1 + lo(x) instead of 1.
///
/// The operand is a RUNTIME value (read from PORTB) so the optimizer cannot
/// constant-fold the augmented assignment away. Protocol: results on PORTD,
/// stage number on PORTE, acks on PORTA pins (same as MulDivModTests).
/// </summary>
[TestFixture]
public class AugAssign16Tests
{
    private const string Source = """
        from pymcu.chips.pic16f877a import PORTA, PORTB, PORTD, PORTE, TRISA, TRISB, TRISD, TRISE
        from pymcu.types import uint8, uint16

        def main():
            TRISB.value = 0xFF
            TRISA.value = 0xFF
            TRISD.value = 0
            TRISE.value = 0
            y: uint8 = PORTB.value
            x: uint16 = y * 60
            x += y
            PORTD.value = x >> 8
            PORTE.value = 1
            while PORTA[5] == 0:
                pass
            PORTD.value = x & 0xFF
            PORTE.value = 2
            while PORTA[4] == 0:
                pass
            w: uint16 = y * 60
            w -= y
            PORTD.value = w >> 8
            PORTE.value = 3
            while PORTA[3] == 0:
                pass
            PORTD.value = w & 0xFF
            PORTE.value = 4
            while True:
                pass
        """;

    private SimSession<Pic16F877ASimulation> _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession<Pic16F877ASimulation>(PymcuCompiler.BuildSource(Source));

    private static void Drive(Gpio port, byte value)
    {
        for (int pin = 0; pin < 8; pin++)
            port.SetInput(pin, (value & (1 << pin)) != 0);
    }

    [Test]
    public void AugAssignAddSub16_With8BitOperand_ZeroExtends()
    {
        var pic = _session.Reset();

        // y = 5 -> x = 300; x += y = 305 = 0x0131; w = 300 - 5 = 295 = 0x0127.
        Drive(pic.PortB, 5);

        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 1);
        pic.PortD.Pins.Should().Be((byte)(305 >> 8), "high byte of 300 + 5 (buggy codegen adds lo(x) into it)");

        pic.PortA.SetInput(5, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 2);
        pic.PortD.Pins.Should().Be((byte)(305 & 0xFF), "low byte of 300 + 5");

        pic.PortA.SetInput(4, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 3);
        pic.PortD.Pins.Should().Be((byte)(295 >> 8), "high byte of 300 - 5 (buggy codegen subtracts lo(x) from it)");

        pic.PortA.SetInput(3, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 4);
        pic.PortD.Pins.Should().Be((byte)(295 & 0xFF), "low byte of 300 - 5");
    }
}
