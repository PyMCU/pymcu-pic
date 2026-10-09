using PicSharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;
using PicSharp.Peripherals;

namespace PyMCU.IntegrationTests.Tests.PIC;

/// <summary>
/// Regression test: the Binary variable-shift path (y = x &lt;&lt; n) looped with
/// <c>DECFSZ n</c> directly on the USER's count variable, destroying it (n == 0
/// after the shift). The count must be copied into the <c>__tmp</c> scratch cell
/// before the loop, like the AugAssign shift paths already do.
///
/// All operands are RUNTIME values (from PORTB) so nothing constant-folds.
/// Protocol: results on PORTD, stage on PORTE, acks on PORTA pins.
/// </summary>
[TestFixture]
public class VarShiftCountTests
{
    private const string Source = """
        from pymcu.chips.pic16f877a import PORTA, PORTB, PORTD, PORTE, TRISA, TRISB, TRISD, TRISE
        from pymcu.types import uint8

        def main():
            TRISB.value = 0xFF
            TRISA.value = 0xFF
            TRISD.value = 0
            TRISE.value = 0
            n: uint8 = PORTB.value & 0x03
            b: uint8 = PORTB.value >> 4
            r: uint8 = b << n
            PORTD.value = r
            PORTE.value = 1
            while PORTA[5] == 0:
                pass
            PORTD.value = n
            PORTE.value = 2
            while PORTA[4] == 0:
                pass
            s: uint8 = b >> n
            PORTD.value = s
            PORTE.value = 3
            while PORTA[3] == 0:
                pass
            PORTD.value = n
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
    public void VariableShift_DoesNotDestroyCountVariable()
    {
        var pic = _session.Reset();

        // PORTB = 0xA3 -> n = 3, b = 10.
        Drive(pic.PortB, 0xA3);

        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 1);
        pic.PortD.Pins.Should().Be((byte)(10 << 3), "10 << 3");

        pic.PortA.SetInput(5, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 2);
        pic.PortD.Pins.Should().Be((byte)3, "n must survive the left shift (buggy DECFSZ left it at 0)");

        pic.PortA.SetInput(4, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 3);
        pic.PortD.Pins.Should().Be((byte)(10 >> 3), "10 >> 3");

        pic.PortA.SetInput(3, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 4);
        pic.PortD.Pins.Should().Be((byte)3, "n must survive the right shift too");
    }
}
