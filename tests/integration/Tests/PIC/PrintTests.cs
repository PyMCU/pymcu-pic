using PicSharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.PIC;

/// <summary>
/// Bare print("literal") on a simulated PIC16F877A — no UART built by hand.
/// The build driver injects the stdout preamble (_pymcu_stdout wires the
/// EUSART via the pymcu.hal.uart facade at 115200 Bd), console.py routes
/// print_str to the pic14 uart_write_str, and the literal comes back out of
/// the flash RETLW tables over serial.
/// </summary>
[TestFixture]
public class PrintTests
{
    private const string Source = """
        def main():
            print("hola flash")
            print("otra linea")
            while True:
                pass
        """;

    private SimSession<Pic16F877ASimulation> _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession<Pic16F877ASimulation>(PymcuCompiler.BuildSource(Source));

    [Test]
    public void Print_LiteralsArriveOnSerial()
    {
        var pic = _session.Reset();
        pic.RunUntilSerial(pic.Serial, "hola flash\notra linea\n");
        pic.Serial.Text.Should().Be("hola flash\notra linea\n",
            "print() must route through the injected stdout preamble and the " +
            "pic14 console dispatcher to the EUSART");
    }
}
