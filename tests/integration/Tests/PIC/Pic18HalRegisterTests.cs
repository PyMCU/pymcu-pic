using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.PIC;

[TestFixture]
public class Pic18HalRegisterTests
{
    private const string UartProgram = """
        from pymcu.types import uint8
        from pymcu.hal.pic18.pic18f45k50_uart import uart_init, uart_write
        from pymcu.chips.pic18f45k50 import LATD, TRISD, PORTB


        def main():
            TRISD.value = 0
            s: uint8 = PORTB.value
            uart_init(9600)
            uart_write(65 + s)
        """;

    private const string AnselProgram = """
        from pymcu.types import uint8
        from pymcu.chips.pic18f45k50 import LATD, TRISD, ANSELD, PORTB


        def main():
            ANSELD.value = 0x00
            TRISD.value = 0
            s: uint8 = PORTB.value
            LATD.value = s
        """;

    private static string Asm(string src) =>
        PymcuCompiler.BuildSourceAsm(src, target: "pic18f45k50", frequencyHz: 16_000_000);

    [Test]
    public void UartInit_LeavesBrg16Clear_SoTheSpbrgTableMatches()
    {
        var asm = Asm(UartProgram);

        asm.Should().MatchRegex(@"MOVLW\s+0x00\s*\n\s*MOVWF\s+0xFB8",
            "BAUDCON must stay 0x00: the SPBRG table is computed for BRG16=0, and setting " +
            "BRG16 made uart_init(9600) run at 38400 instead of 9600 (measured on silicon)");
    }

    [Test]
    public void UartInit_LoadsSpbrgFor9600At16MHz()
    {
        var asm = Asm(UartProgram);

        asm.Should().MatchRegex(@"MOVLW\s+0x67\s*\n\s*MOVWF\s+0xFAF",
            "SPBRG must be 103 (0x67) for 9600 baud at 16 MHz with BRGH=1, BRG16=0");
    }

    [Test]
    public void BankSelect_AssemblesToTheBankItAsksFor()
    {
        var lst = PymcuCompiler.BuildSourceListing(AnselProgram,
            target: "pic18f45k50", frequencyHz: 16_000_000);

        lst.Should().NotContain("Warning[202]",
            "an out-of-range operand is downgraded to a warning and silently truncated");
        lst.Should().MatchRegex(@"010F\s+\d+\s+MOVLB\s+0x0F",
            "bank 15 must assemble to opcode 010F; emitted in decimal it reads as 0x15, " +
            "overflows the 4-bit field and gpasm rewrites it to MOVLB 5 (opcode 0105), " +
            "sending every ANSELx write to bank 5");
    }

    [Test]
    public void AnselD_ResolvesToTheDatasheetAddress()
    {
        var asm = Asm(AnselProgram);

        asm.Should().Contain("0xF5E",
            "ANSELD lives at 0xF5E per p18f45k50.inc; the chip profile had the whole ANSEL " +
            "block at 0xF38-0xF3A, addresses the device does not implement");
    }
}
