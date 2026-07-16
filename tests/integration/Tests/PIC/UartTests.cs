using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.PIC;

/// <summary>
/// End-to-end USART on a simulated PIC16F877A @ 20 MHz: the pymcu.hal.uart
/// facade dispatches to the PIC16F877A implementation, uart_init programs
/// SPBRG from __FREQ__ (129 for 9600 Bd @ 20 MHz, BRGH=1), TX polls TRMT and
/// RX polls RCIF. The firmware prints "PIC\n" and then echoes every received
/// byte incremented by one.
/// </summary>
[TestFixture]
public class UartTests
{
    private const string Source = """
        from pymcu.hal.uart import UART
        from pymcu.types import uint8

        def main():
            uart = UART(9600)
            uart.write(80)
            uart.write(73)
            uart.write(67)
            uart.write(10)
            while True:
                b: uint8 = uart.read()
                uart.write(b + 1)
        """;

    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildSource(Source));

    [Test]
    public void Banner_ArrivesOnSerial()
    {
        var pic = _session.Reset();
        pic.RunUntilSerial(pic.Serial, "PIC\n");
        pic.Serial.Text.Should().StartWith("PIC\n");
    }

    [Test]
    public void Spbrg_ProgrammedFor9600At20MHz()
    {
        var pic = _session.Reset();
        pic.RunUntilSerial(pic.Serial, "PIC\n"); // init has certainly run
        pic.Data[0x99].Should().Be(129, "SPBRG = 20 MHz / (16 * 9600) - 1");
    }

    [Test]
    public void Echo_ReturnsBytePlusOne()
    {
        var pic = _session.Reset();
        pic.RunUntilSerial(pic.Serial, "PIC\n");

        pic.Serial.Send((byte)'A');
        pic.RunUntilSerial(pic.Serial, "B");
        pic.Serial.Text.Should().EndWith("B", "'A' + 1 echoed back");

        pic.Serial.Send((byte)'0');
        pic.RunUntilSerial(pic.Serial, "1");
        pic.Serial.Text.Should().EndWith("1", "'0' + 1 echoed back");
    }
}
