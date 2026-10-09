using PicSharp.TestKit;
using PicSharp.TestKit.Boards;

namespace PyMCU.IntegrationTests;

/// <summary>
/// Manages a shared <typeparamref name="TSim"/> simulation for a single test fixture.
///
/// The simulation is created once per fixture (in <c>[OneTimeSetUp]</c>) with the
/// firmware HEX already loaded into program flash.  Every call to <see cref="Reset"/>
/// restores the simulation to its exact power-on state so each test starts from a
/// clean slate without re-parsing the HEX file.
///
/// Generic on the board, not chip-switched internally, for the same reason
/// <c>PyMCU.OracleRunner.PIC</c>'s own dispatch is a plain switch on the chip name
/// rather than a single shared simulation object (Program.cs): a PIC18F45K50 program
/// built for pic18f45k50 must run on <c>Pic18F45K50Simulation</c>, never on
/// <c>Pic16F877ASimulation</c> -- two different cores with different decoders and SFR
/// maps. Before this, every call site hardcoded <c>Pic16F877ASimulation</c> regardless
/// of which chip the firmware was actually built for, so a PIC18 firmware's machine
/// code ran through the PIC16 (mid-range) instruction decoder: no exception, no crash,
/// just silence -- the simulated UART never produced a byte, because the bytes it WAS
/// producing were never the opcodes PicSharp's mid-range decoder expected.
/// <c>new SimSession&lt;Pic18F45K50Simulation&gt;(hex)</c> now exists specifically so a
/// PIC18-targeted test can ask for the board that actually understands its firmware.
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
public sealed class SimSession<TSim> where TSim : PicSimulation, new()
{
    /// <summary>PIC16F877A reference clock used by the examples/CI: 20 MHz.</summary>
    public const ulong ClockHz = 20_000_000;

    private readonly TSim _sim;
    private readonly byte[] _dataSnapshot;

    /// <summary>
    /// Creates a session for the given firmware HEX content.
    /// </summary>
    /// <param name="hexContent">Intel HEX string returned by <see cref="PymcuCompiler"/>.</param>
    /// <param name="clockHz">
    /// Oscillator frequency the simulation runs at -- must match the <c>frequencyHz</c>
    /// the firmware was BUILT with (baud rates, delay loops and anything else timed off
    /// the clock are computed at compile time from that value). Defaults to the
    /// PIC16F877A reference clock; a PIC18F45K50 session built from a 16 MHz image
    /// must pass 16_000_000 here.
    /// </param>
    public SimSession(string hexContent, ulong clockHz = ClockHz)
    {
        _sim = new TSim();
        _sim.WithFrequency(clockHz);

        // Snapshot taken BEFORE loading the HEX so it captures the peripheral
        // power-on defaults written by peripheral constructors.
        _dataSnapshot = (byte[])_sim.Data.Clone();

        _sim.WithHex(hexContent);
    }

    /// <summary>
    /// Resets the simulation to its power-on state and returns it, ready for a
    /// fresh test run.
    /// </summary>
    public TSim Reset()
    {
        // 1. Restore all SFRs and GPRs to the initial power-on state.
        Array.Copy(_dataSnapshot, _sim.Data, _dataSnapshot.Length);

        // 2. Reset CPU (PC = reset vector, cycle counter to 0), clear pending clock
        //    events, and reset every peripheral.
        _sim.Runner.Reset();

        // 3. Clear the USART receive buffer captured by the serial probe. `Serial` is
        // declared identically on every PicSimulation subclass (SerialProbe Serial { get; })
        // but not promoted to the abstract base itself, so TSim's own constraint
        // (PicSimulation, new()) does not expose it statically -- dynamic dispatch here,
        // not a cast to one concrete board, is what keeps this class generic over any of
        // them.
        ((dynamic)_sim).Serial.Clear();

        return _sim;
    }
}
