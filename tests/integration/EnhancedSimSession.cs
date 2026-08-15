using PicSharp.TestKit.Boards;

namespace PyMCU.IntegrationTests;

/// <summary>
/// <see cref="SimSession"/>'s counterpart for the enhanced mid-range core
/// (PIC14E): owns a single <see cref="Pic16F1939Simulation"/> per fixture and
/// restores it to its exact power-on state on every <see cref="Reset"/>, so tests
/// start clean without re-parsing the firmware HEX. The reset procedure is the
/// same one <see cref="SimSession"/> documents.
/// </summary>
public sealed class EnhancedSimSession
{
    /// <summary>PIC16F1939 reference clock: 32 MHz, the part's maximum.</summary>
    public const ulong ClockHz = 32_000_000;

    private readonly Pic16F1939Simulation _sim;
    private readonly byte[] _dataSnapshot;

    public EnhancedSimSession(string hexContent)
    {
        _sim = new Pic16F1939Simulation();
        _sim.WithFrequency(ClockHz);

        // Snapshot before loading the HEX: captures the peripheral power-on
        // defaults written by the peripheral constructors.
        _dataSnapshot = (byte[])_sim.Data.Clone();

        _sim.WithHex(hexContent);
    }

    public Pic16F1939Simulation Reset()
    {
        Array.Copy(_dataSnapshot, _sim.Data, _dataSnapshot.Length);
        _sim.Runner.Reset();
        _sim.Serial.Clear();
        return _sim;
    }
}
