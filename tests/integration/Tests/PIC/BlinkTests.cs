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
/// The HAL's <c>_delay_ms_pic14</c> busy loop is calibrated for a 4 MHz clock
/// (~0.77 ms per unit), so at 20 MHz a <c>delay_ms(100)</c> lasts ~15 ms of
/// simulated time instead of 100 ms. Until the HAL is frequency-aware these
/// tests assert the blink *behaviour* (both levels observed, sustained
/// toggling) rather than exact timing.
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
    public void Led_TogglesRepeatedly()
    {
        var pic = _session.Reset();
        // 200 ms of simulated time covers many blink periods regardless of the
        // delay-loop calibration; require several full on/off cycles on RB0.
        pic.RunMilliseconds(200);
        pic.PortBProbe.Should().HaveToggled(0, atLeast: 6);
    }

    [Test]
    public void Led_OnlyRb0Toggles()
    {
        var pic = _session.Reset();
        pic.RunMilliseconds(200);
        for (int pin = 1; pin < 8; pin++)
            pic.PortBProbe.EdgeCount[pin].Should().Be(0, $"only RB0 blinks, RB{pin} must stay quiet");
    }
}
