using PicSharp.TestKit.Boards;

// Mirrors the AVR oracle runner's contract (pymcu-avr/tests/oracle/runner/Program.cs) and its
// ARM counterpart (pymcu-arm-oraclesweep/tests/oracle/runner/Program.cs): run a compiled
// firmware image until "END\n" shows up on the simulated UART, or a step budget elapses, and
// print whatever the UART captured (partial output included) so the Python harness can diff
// it against CPython.
//
// Usage: PyMCU.OracleRunner.PIC <chip: pic18f45k50|pic16f877a> <firmware.hex path> <maxSteps>
//
// maxSteps is an INSTRUCTION count, not milliseconds (PicSharp.TestKit's RunUntilSerial takes
// maxSteps, unlike AVR8Sharp's/RP2040Sharp's millisecond-based RunUntilSerial) -- the caller
// picks a budget generous enough for the probe at the chip's own clock.
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: PyMCU.OracleRunner.PIC <pic18f45k50|pic16f877a> <firmware.hex> <maxSteps>");
    return 2;
}

var chip = args[0];
var hexPath = args[1];
var maxSteps = int.Parse(args[2]);
var hex = File.ReadAllText(hexPath);

switch (chip)
{
    case "pic16f877a":
    {
        var sim = new Pic16F877ASimulation();
        sim.WithFrequency(20_000_000);
        sim.WithHex(hex);
        try
        {
            sim.RunUntilSerial(sim.Serial, "END\n", maxSteps);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[runner] pic16f877a run raised: {ex}");
        }
        Console.Write(sim.Serial.Text.Replace("\r\n", "\n"));
        return 0;
    }
    case "pic18f45k50":
    {
        var sim = new Pic18F45K50Simulation();
        sim.WithFrequency(16_000_000);
        sim.WithHex(hex);
        try
        {
            sim.RunUntilSerial(sim.Serial, "END\n", maxSteps);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[runner] pic18f45k50 run raised: {ex}");
        }
        Console.Write(sim.Serial.Text.Replace("\r\n", "\n"));
        return 0;
    }
    default:
        Console.Error.WriteLine($"[runner] unknown chip '{chip}'");
        return 2;
}
