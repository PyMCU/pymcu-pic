using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.PIC;

/// <summary>
/// End-to-end flash strings (M3) on a simulated PIC16F877A: const[str]
/// literals are interned as RETLW tables in program memory and read back with
/// a computed GOTO (PCLATH:W -> PCL).
///
/// Coverage:
/// - UART.write_str / println: the literal is passed BY REFERENCE
///   (FlashStrAddr, a 16-bit flash word address) into the shared
///   uart_write_str subroutine, which walks it with FlashLoadPtr through the
///   __flash_jmp trampoline.
/// - for ch in "literal": direct table access via ArrayLoadFlash (per-table
///   computed-goto prologue).
/// </summary>
[TestFixture]
public class FlashStringTests
{
    private const string Source = """
        from pymcu.hal.uart import UART

        def main():
            uart = UART(9600)
            uart.write_str("hola ")
            uart.println("mundo")
            for ch in "PIC14":
                uart.write(ch)
            uart.write(10)
            while True:
                pass
        """;

    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildSource(Source));

    [Test]
    public void FlashStrings_ArriveOnSerial()
    {
        var pic = _session.Reset();
        pic.RunUntilSerial(pic.Serial, "hola mundo\nPIC14\n");
        pic.Serial.Text.Should().Be("hola mundo\nPIC14\n",
            "write_str/println read the RETLW tables via FlashLoadPtr and " +
            "string iteration reads them via ArrayLoadFlash");
    }
}
