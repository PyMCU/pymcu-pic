using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.PIC;

[TestFixture]
public class Pic18UartFacadeTests
{
    private static string Asm(string source) =>
        PymcuCompiler.BuildSourceAsm(source, target: "pic18f45k50", frequencyHz: 16_000_000);

    [Test]
    public void Uart_ResolvesToTheChipImplementation_NotTheStub()
    {
        var asm = Asm("""
            from pymcu.hal.pic18.uart import UART


            def main():
                u = UART(9600)
                u.write(65)
            """);

        asm.Should().MatchRegex(@"MOVLW\s+0x67",
            "SPBRG must be programmed for 9600 baud at 16 MHz; the stub uart_init was a no-op");
        asm.Should().MatchRegex(@"MOVLW\s+0x24",
            "TXSTA must enable TXEN and BRGH");
        asm.Should().MatchRegex(@"MOVLW\s+0x90",
            "RCSTA must enable SPEN and CREN");
    }

    [Test]
    public void Print_ReachesTheUart()
    {
        var asm = Asm("""
            from pymcu.hal.pic18.uart import UART


            def main():
                u = UART(9600)
                print("hi")
            """);

        asm.Should().Contain("uart_write_str",
            "print() lowers to the console facade, which had no pic18 branch and dropped the literal");
    }
}
