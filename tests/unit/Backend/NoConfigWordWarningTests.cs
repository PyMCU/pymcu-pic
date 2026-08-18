using PyMCU.Backend.Targets.PIC14;
using PyMCU.Backend.Targets.PIC18;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

public class NoConfigWordWarningTests
{
    private static ProgramIR MainOnly()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction> { new Return(new NoneVal()) },
        });

        return prog;
    }

    private static string CompileCapturingStderr(DeviceConfig cfg, bool pic18)
    {
        var previous = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            var sw = new StringWriter();
            if (pic18) new PIC18CodeGen(cfg).Compile(MainOnly(), sw);
            else new PIC14CodeGen(cfg).Compile(MainOnly(), sw);
            return captured.ToString();
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    [Fact]
    public void Pic14ChipWithoutFuses_WarnsInsteadOfShippingABlankConfigWord()
    {
        var err = CompileCapturingStderr(
            new DeviceConfig { Chip = "pic16f877a", Arch = "pic14" }, pic18: false);

        Assert.Contains("no config word emitted for pic16f877a", err);
        Assert.Contains("will not boot", err);
    }

    [Fact]
    public void Pic14ChipWithFuses_DoesNotWarn()
    {
        var err = CompileCapturingStderr(
            new DeviceConfig { Chip = "pic16f628a", Arch = "pic14" }, pic18: false);

        Assert.DoesNotContain("no config word emitted for pic16f628a", err);
    }

    [Fact]
    public void Pic18ChipWithoutFuses_WarnsInsteadOfShippingABlankConfigWord()
    {
        var err = CompileCapturingStderr(
            new DeviceConfig { Chip = "pic18f2550", Arch = "pic18" }, pic18: true);

        Assert.Contains("no config word emitted for pic18f2550", err);
    }

    [Fact]
    public void Pic18ReferenceChip_DoesNotWarn()
    {
        var err = CompileCapturingStderr(
            new DeviceConfig { Chip = "pic18f45k50", Arch = "pic18" }, pic18: true);

        Assert.DoesNotContain("no config word emitted for pic18f45k50", err);
    }
}
