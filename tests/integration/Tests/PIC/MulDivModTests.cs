using PicSharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;
using PicSharp.Peripherals;

namespace PyMCU.IntegrationTests.Tests.PIC;

/// <summary>
/// End-to-end software multiply/divide/modulo on a simulated PIC16F877A.
/// The operands are RUNTIME values (read from PORTB/PORTA input pins driven by
/// the test), so the compiler cannot constant-fold them: every result must come
/// out of the emitted __mul8/__div8/__mul16/__div16 routines.
///
/// Protocol: the firmware publishes each result on PORTD and announces the
/// stage number on PORTE (3 bits); the test acknowledges by raising one PORTA
/// pin per stage.
/// </summary>
[TestFixture]
public class MulDivModTests
{
    private const string Source = """
        from pymcu.chips.pic16f877a import PORTA, PORTB, PORTD, PORTE, TRISA, TRISB, TRISD, TRISE
        from pymcu.types import uint8, uint16

        def main():
            TRISB.value = 0xFF
            TRISA.value = 0xFF
            TRISD.value = 0
            TRISE.value = 0
            a: uint8 = PORTB.value
            b: uint8 = PORTA.value & 0x0F
            PORTD.value = a * b
            PORTE.value = 1
            while PORTA[5] == 0:
                pass
            PORTD.value = a // b
            PORTE.value = 2
            while PORTA[4] == 0:
                pass
            PORTD.value = a % b
            PORTE.value = 3
            while PORTA[3] == 0:
                pass
            w: uint16 = a * 251
            PORTD.value = w // 300
            PORTE.value = 4
            while PORTA[2] == 0:
                pass
            PORTD.value = w % 300
            PORTE.value = 5
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
    public void MulDivMod_RuntimeOperands_ExactResults()
    {
        var pic = _session.Reset();

        // a = 229, b = 13 (PORTA reads only its low nibble as the divisor).
        Drive(pic.PortB, 229);
        Drive(pic.PortA, 13);

        // Stage 1: 8-bit multiply -> (229 * 13) & 0xFF = 2977 & 0xFF = 161.
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 1);
        (pic.PortE.Pins & 0x07).Should().Be(1, "firmware must reach stage 1");
        pic.PortD.Pins.Should().Be((byte)161, "229 * 13 truncated to 8 bits");

        // Stage 2: 8-bit floor division -> 229 // 13 = 17.
        pic.PortA.SetInput(5, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 2);
        pic.PortD.Pins.Should().Be((byte)17, "229 // 13");

        // Stage 3: 8-bit modulo -> 229 % 13 = 8.
        pic.PortA.SetInput(4, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 3);
        pic.PortD.Pins.Should().Be((byte)8, "229 % 13");

        // Stage 4: 16-bit divide -> (229 * 251) // 300 = 57479 // 300 = 191.
        pic.PortA.SetInput(3, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 4);
        pic.PortD.Pins.Should().Be((byte)191, "57479 // 300");

        // Stage 5: 16-bit modulo -> 57479 % 300 = 179.
        pic.PortA.SetInput(2, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 5);
        pic.PortD.Pins.Should().Be((byte)179, "57479 % 300");
    }

    [Test]
    public void MulDivMod_DifferentSeed_StillExact()
    {
        var pic = _session.Reset();

        // a = 200, b = 7.
        Drive(pic.PortB, 200);
        Drive(pic.PortA, 7);

        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 1);
        pic.PortD.Pins.Should().Be((byte)((200 * 7) & 0xFF), "200 * 7 truncated");

        pic.PortA.SetInput(5, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 2);
        pic.PortD.Pins.Should().Be((byte)(200 / 7), "200 // 7");

        pic.PortA.SetInput(4, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 3);
        pic.PortD.Pins.Should().Be((byte)(200 % 7), "200 % 7");

        pic.PortA.SetInput(3, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 4);
        pic.PortD.Pins.Should().Be((byte)(200 * 251 / 300), "50200 // 300");

        pic.PortA.SetInput(2, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 5);
        pic.PortD.Pins.Should().Be((byte)(200 * 251 % 300), "50200 % 300");
    }
}
