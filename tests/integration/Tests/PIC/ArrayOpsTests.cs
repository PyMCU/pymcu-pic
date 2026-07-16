using FluentAssertions;
using NUnit.Framework;
using PicSharp.Peripherals;

namespace PyMCU.IntegrationTests.Tests.PIC;

/// <summary>
/// End-to-end RAM array access on a simulated PIC16F877A: constant-index
/// loads/stores (direct addressing) and runtime-index loads/stores (FSR/INDF).
/// The index and the loop bound come from PORTB input pins so the optimizer
/// cannot constant-fold or unroll them.
///
/// Protocol: results on PORTD, stage number on PORTE, acks on PORTA pins.
/// </summary>
[TestFixture]
public class ArrayOpsTests
{
    private const string Source = """
        from pymcu.chips.pic16f877a import PORTA, PORTB, PORTD, PORTE, TRISA, TRISB, TRISD, TRISE
        from pymcu.types import uint8

        def main():
            TRISB.value = 0xFF
            TRISA.value = 0xFF
            TRISD.value = 0
            TRISE.value = 0
            buf: uint8[8] = [10, 20, 30, 40, 50, 60, 70, 80]
            k: uint8 = PORTB.value & 0x07
            PORTD.value = buf[k]
            PORTE.value = 1
            while PORTA[5] == 0:
                pass
            buf[k] = 123
            PORTD.value = buf[5]
            PORTE.value = 2
            while PORTA[4] == 0:
                pass
            buf[2] = 99
            total: uint8 = 0
            i: uint8 = 0
            while i < k:
                total = total + buf[i]
                i += 1
            PORTD.value = total
            PORTE.value = 3
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
    public void RuntimeAndConstIndexAccess_ExactValues()
    {
        var pic = _session.Reset();
        Drive(pic.PortB, 5); // k = 5

        // Stage 1: runtime-index load -> buf[5] = 60.
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 1);
        (pic.PortE.Pins & 0x07).Should().Be(1, "firmware must reach stage 1");
        pic.PortD.Pins.Should().Be((byte)60, "buf[k] with k=5 read through FSR/INDF");

        // Stage 2: runtime-index store buf[5]=123, verified by a CONST-index load.
        pic.PortA.SetInput(5, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 2);
        pic.PortD.Pins.Should().Be((byte)123, "buf[k]=123 must land in buf[5]");

        // Stage 3: const-index store buf[2]=99, then sum buf[0..k) with a
        // runtime loop bound: 10 + 20 + 99 + 40 + 50 = 219.
        pic.PortA.SetInput(4, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 3);
        pic.PortD.Pins.Should().Be((byte)219, "sum of buf[0..5) after buf[2]=99");
    }

    [Test]
    public void DifferentRuntimeIndex_HitsOtherSlot()
    {
        var pic = _session.Reset();
        Drive(pic.PortB, 2); // k = 2

        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 1);
        pic.PortD.Pins.Should().Be((byte)30, "buf[2]");

        pic.PortA.SetInput(5, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 2);
        pic.PortD.Pins.Should().Be((byte)60, "buf[5] untouched when k=2");

        // Sum of buf[0..2) after buf[2]=123 then buf[2]=99: 10 + 20 = 30.
        pic.PortA.SetInput(4, true);
        pic.RunUntil(_ => (pic.PortE.Pins & 0x07) == 3);
        pic.PortD.Pins.Should().Be((byte)30, "sum of buf[0..2)");
    }
}
