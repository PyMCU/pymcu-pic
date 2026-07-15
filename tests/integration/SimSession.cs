using PicSharp.TestKit.Boards;

namespace PyMCU.IntegrationTests;

/// <summary>
/// Manages a shared <see cref="Pic16F877ASimulation"/> for a single test fixture.
///
/// The simulation is created once per fixture (in <c>[OneTimeSetUp]</c>) with the
/// firmware HEX already loaded into program flash.  Every call to <see cref="Reset"/>
/// restores the simulation to its exact power-on state so each test starts from a
/// clean slate without re-parsing the HEX file.
/// </summary>
/// <remarks>
/// Reset procedure:
/// <list type="bullet">
///   <item>Restore the flat <c>Data</c> array (SFRs + GPRs) from a snapshot captured
///         immediately after construction — before any firmware has run — preserving
///         the power-on values written by peripheral constructors.</item>
///   <item>Call <c>Runner.Reset()</c>: CPU state to power-on, PC to the reset vector,
///         pending clock events cleared, and every peripheral reset.</item>
/// </list>
/// </remarks>
public sealed class SimSession
{
    /// <summary>PIC16F877A reference clock used by the examples/CI: 20 MHz.</summary>
    public const ulong ClockHz = 20_000_000;

    private readonly Pic16F877ASimulation _sim;
    private readonly byte[] _dataSnapshot;

    /// <summary>
    /// Creates a session for the given firmware HEX content.
    /// </summary>
    /// <param name="hexContent">Intel HEX string returned by <see cref="PymcuCompiler"/>.</param>
    public SimSession(string hexContent)
    {
        _sim = new Pic16F877ASimulation();
        _sim.WithFrequency(ClockHz);

        // Snapshot taken BEFORE loading the HEX so it captures the peripheral
        // power-on defaults written by peripheral constructors.
        _dataSnapshot = (byte[])_sim.Data.Clone();

        _sim.WithHex(hexContent);
    }

    /// <summary>
    /// Resets the simulation to its power-on state and returns it, ready for a
    /// fresh test run.
    /// </summary>
    public Pic16F877ASimulation Reset()
    {
        // 1. Restore all SFRs and GPRs to the initial power-on state.
        Array.Copy(_dataSnapshot, _sim.Data, _dataSnapshot.Length);

        // 2. Reset CPU (PC = reset vector, cycle counter to 0), clear pending clock
        //    events, and reset every peripheral.
        _sim.Runner.Reset();

        // 3. Clear the USART receive buffer captured by the serial probe.
        _sim.Serial.Clear();

        return _sim;
    }
}
