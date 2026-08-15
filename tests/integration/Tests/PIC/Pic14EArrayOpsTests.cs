using FluentAssertions;
using NUnit.Framework;
using PicSharp.Peripherals;

namespace PyMCU.IntegrationTests.Tests.PIC;

/// <summary>
/// End-to-end RAM array access on the enhanced mid-range core (PIC14E), running
/// on a simulated PIC16F1939. This is the FSR0/INDF0 path: a 16-bit index
/// register, no STATUS&lt;IRP&gt; bit, and MOVIW/MOVWI for the auto-incrementing
/// steps — the legacy FSR/INDF encoding does not even assemble on a PIC16F1xxxx.
///
/// <para>
/// The chip definition travels with the test: the compiler resolves
/// <c>pymcu.chips.*</c> from the project sources before the installed stdlib, and
/// the stdlib carries no PIC16F1939 module. That also keeps the test pinned to a
/// part PicSharp emulates, which the stdlib's only enhanced chip (PIC16F18877) is
/// not — see the README for what that leaves unvalidated.
/// </para>
///
/// <para>
/// The index, the loop bound and the stage acknowledgements all come from PORTB
/// pins (index in bits 0-2, acks in bits 4-7) so the optimizer cannot
/// constant-fold or unroll any of it. Results land on LATA, the stage number on
/// LATC.
/// </para>
/// </summary>
[TestFixture]
public class Pic14EArrayOpsTests
{
    private const string ChipModule = """
        from pymcu.types import ptr, uint8, device_info

        RAM_START = 0x20
        RAM_SIZE = 1024

        device_info(chip="pic16f1939", arch="pic14e", ram_size=RAM_SIZE)

        PORTA: ptr[uint8] = ptr(0x000C)
        PORTB: ptr[uint8] = ptr(0x000D)
        PORTC: ptr[uint8] = ptr(0x000E)
        TRISA: ptr[uint8] = ptr(0x008C)
        TRISB: ptr[uint8] = ptr(0x008D)
        TRISC: ptr[uint8] = ptr(0x008E)
        LATA:  ptr[uint8] = ptr(0x010C)
        LATB:  ptr[uint8] = ptr(0x010D)
        LATC:  ptr[uint8] = ptr(0x010E)
        """;

    private const string Source = """
        from pymcu.chips.pic16f1939 import PORTB, TRISA, TRISB, TRISC, LATA, LATC
        from pymcu.types import uint8, uint16

        def main():
            TRISA.value = 0
            TRISC.value = 0
            TRISB.value = 0xFF
            buf: uint8[8] = [10, 20, 30, 40, 50, 60, 70, 80]
            wide: uint16[4] = [1000, 2000, 3000, 4000]

            k: uint8 = PORTB.value & 0x07
            LATA.value = buf[k]
            LATC.value = 1
            while PORTB[7] == 0:
                pass

            buf[k] = 123
            LATA.value = buf[5]
            LATC.value = 2
            while PORTB[6] == 0:
                pass

            total: uint8 = 0
            i: uint8 = 0
            while i < k:
                total = total + buf[i]
                i += 1
            LATA.value = total
            LATC.value = 3
            while PORTB[5] == 0:
                pass

            j: uint8 = k & 0x03
            w: uint16 = wide[j]
            LATA.value = w >> 8
            LATC.value = 4
            while PORTB[4] == 0:
                pass

            n: uint16 = 0xBEEF
            wide[j] = n
            v: uint16 = wide[j]
            LATA.value = v & 0xFF
            LATC.value = 5
            while PORTB[3] == 0:
                pass

            LATA.value = v >> 8
            LATC.value = 6
            while True:
                pass
        """;

    private EnhancedSimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new EnhancedSimSession(
        PymcuCompiler.BuildSource(Source, target: "pic16f1939", frequencyHz: 32_000_000,
            extraSources: new Dictionary<string, string>
            {
                ["pymcu/chips/pic16f1939.py"] = ChipModule,
            }));

    private static void Drive(Gpio port, byte value)
    {
        for (int pin = 0; pin < 8; pin++)
            port.SetInput(pin, (value & (1 << pin)) != 0);
    }

    // k, buf[k], sum of buf[0..k), high byte of the initial wide[k & 3]
    // (1000 / 2000 / 4000 -> 0x03 / 0x07 / 0x0F).
    [TestCase((byte)5, (byte)60, (byte)150, (byte)0x07)]
    [TestCase((byte)0, (byte)10, (byte)0, (byte)0x03)]
    [TestCase((byte)3, (byte)40, (byte)60, (byte)0x0F)]
    public void ArrayAccess_ThroughFsr0_ExactValues(
        byte k, byte firstElement, byte sum, byte initialWideHigh)
    {
        var pic = _session.Reset();
        Drive(pic.PortB, k);

        // Stage 1: runtime-index load.
        pic.RunUntil(_ => (pic.PortC.Pins & 0x07) == 1);
        (pic.PortC.Pins & 0x07).Should().Be(1, "firmware must reach stage 1");
        pic.PortA.Pins.Should().Be(firstElement, "buf[k] read through FSR0/INDF0");

        // Stage 2: runtime-index store buf[k]=123, read back with a CONST index.
        // Only k = 5 targets buf[5]; the other cases prove the store did not spill
        // into a neighbouring element.
        pic.PortB.SetInput(7, true);
        pic.RunUntil(_ => (pic.PortC.Pins & 0x07) == 2);
        pic.PortA.Pins.Should().Be(k == 5 ? (byte)123 : (byte)60,
            "the runtime-index store must hit exactly buf[k]");

        // Stage 3: loop traversal with a runtime bound, summing buf[0..k).
        pic.PortB.SetInput(6, true);
        pic.RunUntil(_ => (pic.PortC.Pins & 0x07) == 3);
        pic.PortA.Pins.Should().Be(sum, "sum of buf[0..k) walked through FSR0");

        // Stage 4: 16-bit element, runtime-index load. The high byte is the half
        // fetched by MOVIW ++FSR0, and it differs per index, so a pointer off by
        // one element would show up here.
        pic.PortB.SetInput(5, true);
        pic.RunUntil(_ => (pic.PortC.Pins & 0x07) == 4);
        pic.PortA.Pins.Should().Be(initialWideHigh, "high byte of the initial wide[j]");

        // Stage 5: 16-bit runtime-index store (MOVWI FSR0++ for the low half),
        // read back through a second runtime-index load.
        pic.PortB.SetInput(4, true);
        pic.RunUntil(_ => (pic.PortC.Pins & 0x07) == 5);
        pic.PortA.Pins.Should().Be((byte)0xEF, "low byte of wide[j] after storing 0xBEEF");

        // Stage 6: high byte of that same element.
        pic.PortB.SetInput(3, true);
        pic.RunUntil(_ => (pic.PortC.Pins & 0x07) == 6);
        pic.PortA.Pins.Should().Be((byte)0xBE, "high byte of wide[j] after storing 0xBEEF");
    }
}
