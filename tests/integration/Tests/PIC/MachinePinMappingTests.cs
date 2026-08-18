using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.PIC;

[TestFixture]
public class MachinePinMappingTests
{
    private const string IntegerPinProgram = """
        from machine import Pin


        def main():
            led = Pin(13, Pin.OUT)
            while True:
                led.value(1)
                led.value(0)
        """;

    [Test]
    public void IntegerPinNumber_OnPic_IsRejectedAtCompileTime()
    {
        var build = () => PymcuCompiler.BuildSource(
            IntegerPinProgram, target: "pic18f45k50", frequencyHz: 16_000_000, stdlib: "micropython");

        build.Should().Throw<InvalidOperationException>(
                "a PIC has no Arduino board pin map, so Pin(13) must not silently resolve to an AVR port name")
            .WithMessage("*no board pin map*");
    }

    [Test]
    public void IntegerPinNumber_OnPic_DoesNotLeakAvrPortNames()
    {
        var build = () => PymcuCompiler.BuildSource(
            IntegerPinProgram, target: "pic18f45k50", frequencyHz: 16_000_000, stdlib: "micropython");

        build.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().NotContain("PB5",
                "resolving an integer pin to an AVR port name on a PIC target is the bug under test");
    }

    private const string PortNamePinProgram = """
        from machine import Pin


        def main():
            led = Pin("RA4", Pin.OUT)
            while True:
                led.value(1)
                led.value(0)
        """;

    [Test]
    public void PortNamePin_OnPic_Compiles()
    {
        var build = () => PymcuCompiler.BuildSource(
            PortNamePinProgram, target: "pic18f45k50", frequencyHz: 16_000_000, stdlib: "micropython");

        // The port name is the form the integer-pin error tells the caller to use, so it
        // has to work. Overload keys spell const[...] types raw while the call site spells
        // them normalized, so the exact lookup missed and arity alone chose the overload
        // declared first — the integer one, which then rejected the string with the very
        // advice the caller had followed.
        build.Should().NotThrow(
            "Pin(\"RA4\", Pin.OUT) is exactly what the integer-pin diagnostic asks for");
    }
}
