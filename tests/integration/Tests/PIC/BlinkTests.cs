using FluentAssertions;
using NUnit.Framework;
using PicSharp.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.PIC;

/// <summary>
/// Integration tests for examples/blink on a simulated PIC16F877A @ 20 MHz.
/// Hardware: LED on RB0. No button, no UART.
/// Logic: led.high() → delay_ms(100) → led.low() → delay_ms(100) → repeat.
/// </summary>
/// <remarks>
/// The HAL's <c>_delay_ms_pic14</c> busy loop is calibrated per <c>__FREQ__</c>
/// (~499 Tcy per inner turn), so <c>delay_ms(100)</c> at 20 MHz lasts ~100 ms
/// of simulated time. The window assertions are deliberately loose (±40%):
/// they catch calibration being off by 2x or more (e.g. the old 4 MHz-only
/// loop ran 6.5x too fast at 20 MHz) without turning into cycle-exact tests.
/// </remarks>
[TestFixture]
public class BlinkTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.Build("blink"));

    [Test]
    public void Led_StartsHighAfterBoot()
    {
        var pic = _session.Reset();
        // TRISB0=0 + PORTB0=1 execute within the first instructions.
        pic.RunMilliseconds(1);
        pic.PortB.Should().HavePinHigh(0);
    }

    [Test]
    public void Led_GoesLowAfterFirstDelay()
    {
        var pic = _session.Reset();
        // Run until the LED drops for the first time (bounded); proves the
        // first delay terminates and led.low() executes.
        pic.RunUntil(_ => !pic.PortBProbe.Pin(0), maxSteps: 2_000_000);
        pic.PortBProbe.Pin(0).Should().BeFalse();
    }

    [Test]
    public void FirstDelay_LastsRoughly100ms()
    {
        var pic = _session.Reset();
        pic.RunUntil(_ => pic.PortBProbe.Pin(0), maxSteps: 2_000_000);   // led.high()
        ulong start = pic.Cycles;
        pic.RunUntil(_ => !pic.PortBProbe.Pin(0), maxSteps: 4_000_000);  // after delay_ms(100)

        // delay_ms(100) at 20 MHz = ~500_000 instruction cycles. Accept 60-140 ms.
        double ms = (pic.Cycles - start) * 4_000.0 / SimSession.ClockHz;
        ms.Should().BeInRange(60, 140, "delay_ms(100) must be calibrated for __FREQ__ = 20 MHz");
    }

    [Test]
    public void Led_TogglesRepeatedly()
    {
        var pic = _session.Reset();
        // 1.05 s of simulated time = ~10 edges at a 100 ms half-period.
        pic.RunMilliseconds(1050);
        pic.PortBProbe.Should().HaveToggled(0, atLeast: 6);
        pic.PortBProbe.EdgeCount[0].Should().BeLessThan(20,
            "a 100 ms half-period cannot produce more than ~10 edges per second");
    }

    [Test]
    public void Led_OnlyRb0Toggles()
    {
        var pic = _session.Reset();
        pic.RunMilliseconds(300);
        for (int pin = 1; pin < 8; pin++)
            pic.PortBProbe.EdgeCount[pin].Should().Be(0, $"only RB0 blinks, RB{pin} must stay quiet");
    }
}
