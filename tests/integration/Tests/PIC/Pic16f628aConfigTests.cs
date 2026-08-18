using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.PIC;

[TestFixture]
public class Pic16f628aConfigTests
{
    private const string Source = """
        from pymcu.types import uint8
        from pymcu.chips.pic16f628a import PORTB, TRISB


        def main():
            TRISB.value = 0
            while True:
                PORTB.value = 0xFF
                PORTB.value = 0x00
        """;

    [Test]
    public void TheConfigWord_AssemblesToTheValueTheBoardNeeds()
    {
        var lst = PymcuCompiler.BuildSourceListing(Source, target: "pic16f628a", frequencyHz: 4_000_000);

        lst.Should().MatchRegex(@"2007\s+3F30",
            "erased flash leaves 0x3FFF: RC oscillator on a board with no RC network, "
            + "watchdog on, and LVP stealing RB4 -- the part would not run");
    }
}
