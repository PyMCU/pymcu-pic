using FluentAssertions;
using NUnit.Framework;
using PicSharp.TestKit.Boards;

namespace PyMCU.IntegrationTests.Tests.PIC;

/// <summary>
/// Smoke test on a second PIC14 part: blink compiled for the PIC16F84A @ 4 MHz
/// and executed on <see cref="Pic16F84ASimulation"/>. Exercises the 84A chip
/// module, its GPIO HAL, the 4 MHz delay calibration, and the smaller RAM
/// layout (GPRs 0x0C-0x4F) end-to-end.
/// </summary>
[TestFixture]
public class Pic16F84ABlinkTests
{
    private const ulong ClockHz = 4_000_000;

    private const string Source = """
        from pymcu.hal.gpio import Pin
        from pymcu.time import delay_ms

        def main():
            led = Pin("RB0", Pin.OUT)
            while True:
                led.high()
                delay_ms(50)
                led.low()
                delay_ms(50)
        """;

    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() =>
        _hex = PymcuCompiler.BuildSource(Source, target: "pic16f84a", frequencyHz: (int)ClockHz);

    private Pic16F84ASimulation CreateSim()
    {
        var sim = new Pic16F84ASimulation();
        sim.WithFrequency(ClockHz);
        sim.WithHex(_hex);
        return sim;
    }

    [Test]
    public void Led_StartsHighAfterBoot()
    {
        var pic = CreateSim();
        pic.RunMilliseconds(1);
        pic.PortBProbe.Pin(0).Should().BeTrue("RB0 goes high right after boot");
    }

    [Test]
    public void Led_TogglesRepeatedly()
    {
        var pic = CreateSim();
        // 1.05 s at a 50 ms half-period = ~20 edges; require at least half.
        pic.RunMilliseconds(1050);
        pic.PortBProbe.EdgeCount[0].Should().BeGreaterThanOrEqualTo(10,
            "the 84A blink must keep toggling RB0");
    }

    [Test]
    public void FirstDelay_LastsRoughly50ms()
    {
        var pic = CreateSim();
        pic.RunUntil(_ => pic.PortBProbe.Pin(0), maxSteps: 1_000_000);
        ulong start = pic.Cycles;
        pic.RunUntil(_ => !pic.PortBProbe.Pin(0), maxSteps: 1_000_000);

        double ms = (pic.Cycles - start) * 4_000.0 / ClockHz;
        ms.Should().BeInRange(30, 70, "delay_ms(50) must be calibrated for __FREQ__ = 4 MHz");
    }
}
