using FluentAssertions;
using NUnit.Framework;
using PicSharp.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.PIC;

/// <summary>
/// End-to-end arithmetic on a simulated PIC16F877A: a generated program
/// computes a value (loop accumulation + XOR) and publishes it on PORTD,
/// exercising Copy/Binary/AugAssign/JumpIf* through the real
/// pymcu → pymcuc-pic → gpasm pipeline.
/// </summary>
[TestFixture]
public class ArithmeticTests
{
    // sum(1..10) = 55; 55 ^ 0x0F = 56.
    private const string Source = """
        from pymcu.chips.pic16f877a import PORTD, TRISD
        from pymcu.types import uint8

        def main():
            TRISD.value = 0
            total: uint8 = 0
            i: uint8 = 1
            while i <= 10:
                total = total + i
                i = i + 1
            total = total ^ 0x0F
            while True:
                PORTD.value = total
        """;

    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildSource(Source));

    [Test]
    public void SumAndXor_AppearsOnPortD()
    {
        var pic = _session.Reset();
        pic.RunMilliseconds(5);
        pic.PortD.Should().HaveValue(56);
    }
}
