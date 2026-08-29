using System.Text.RegularExpressions;
using PyMCU.Backend.Targets.PIC12;
using PyMCU.Backend.Targets.PIC14;
using PyMCU.Backend.Targets.PIC18;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

// A label defined inside an `asm()` block is renamed per expansion, so a body inlined twice
// does not define the same label twice. The renaming grouped ADJACENT InlineAsm instructions,
// and since PyMCU 0ea529e4 a DebugLine sits between every `asm()` of an inlined body: every
// run held one instruction, the definition was renamed inside its own run, and the jump in the
// next run kept the old name.
//
//     _dly_i16m18_1:                  <- the definition, renamed
//         DECFSZ __dly_c1, F, ACCESS
//         BRA _dly_i16m18             <- the jump, with the old name
//
// gpasm: Error[113] Symbol not previously defined. `blink` stopped assembling on every PIC
// chip of all three families, and pic10f200 and pic16f84a lost the only corpus program they
// were building.
//
// WHAT DISCRIMINATES: `EveryJumpTargetIsDefined` on each of the three targets. Against the
// unfixed backend the jump names a label the output never defines.
//
// WHAT IT ASSERTS, and why not the suffix: the property gpasm checks, that every label a jump
// names is a label the output defines. Asserting `_dly_0` would pin the naming scheme instead
// of the requirement, and would pass just as well if the renamer stopped renaming at all.
//
// WHAT IS INVARIANT, and here on purpose: `TwoExpansionsStillGetDistinctLabels`, which passes
// against the unfixed backend too. Widening the run past a real instruction would satisfy the
// discriminator above by merging the two expansions into one and defining the label twice,
// which is the failure the renamer exists to prevent. The test nearest this one,
// `InlineAsmLabels_AreUniquePerExpansion`, checks exactly that and never looks at the jump,
// which is how a dangling reference stayed green in a suite that had a test for this code.
public class InlineAsmLabelRunTests
{
    // Two expansions of one asm block, each with a DebugLine between the label and the jump,
    // separated by a real instruction. This is the shape `delay_ms` lowers to.
    private static ProgramIR TwoExpansions(string jump) =>
        Make("main",
            new InlineAsm("_dly:"),
            new DebugLine(165, "asm(\"    MOVLW 0xDD\")", "time.py", true),
            new InlineAsm($"\t{jump} _dly"),
            new Copy(new Constant(1), new Variable("x")),
            new InlineAsm("_dly:"),
            new DebugLine(165, "asm(\"    MOVLW 0xDD\")", "time.py", true),
            new InlineAsm($"\t{jump} _dly"),
            new Return(new NoneVal()));

    private static ProgramIR Make(string name, params Instruction[] body)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = name, Body = body.ToList() });
        return prog;
    }

    private static string Pic12(ProgramIR p) => Run(new PIC12CodeGen(
        new DeviceConfig { Chip = "pic10f200", Arch = "pic12" }).Compile, p);

    private static string Pic14(ProgramIR p) => Run(new PIC14CodeGen(
        new DeviceConfig { Chip = "pic16f84a", Arch = "pic14" }).Compile, p);

    private static string Pic18(ProgramIR p) => Run(new PIC18CodeGen(
        new DeviceConfig { Chip = "pic18f45k50", Arch = "pic18" }).Compile, p);

    private static string Run(Action<ProgramIR, TextWriter> compile, ProgramIR p)
    {
        var sw = new StringWriter();
        compile(p, sw);
        return sw.ToString();
    }

    private static readonly Regex Definition = new(@"^\s*(_dly[A-Za-z0-9_]*)\s*:");
    private static readonly Regex Jump = new(@"\b(?:GOTO|BRA)\s+(_dly[A-Za-z0-9_]*)\b");

    private static (List<string> defined, List<string> jumped) Labels(string asm)
    {
        var defined = new List<string>();
        var jumped = new List<string>();
        foreach (var raw in asm.Split('\n'))
        {
            var line = raw.Split(';')[0];   // a DebugLine comment quotes the ORIGINAL asm text
            var d = Definition.Match(line);
            if (d.Success) { defined.Add(d.Groups[1].Value); continue; }
            var j = Jump.Match(line);
            if (j.Success) jumped.Add(j.Groups[1].Value);
        }
        return (defined, jumped);
    }

    // --- what discriminates ----------------------------------------------------

    [Theory]
    [InlineData("pic12")]
    [InlineData("pic14")]
    [InlineData("pic18")]
    public void EveryJumpTargetIsDefined(string target)
    {
        var (defined, jumped) = Labels(Emit(target));

        Assert.Equal(2, jumped.Count);
        foreach (var name in jumped)
            Assert.True(defined.Contains(name),
                $"{target}: the jump names '{name}', which the output never defines. " +
                $"Defined: {string.Join(", ", defined)}");

        // Each expansion jumps to its own copy, not both to the same one.
        Assert.NotEqual(jumped[0], jumped[1]);
    }

    // --- invariant: the renaming still has to do its own job -------------------

    [Theory]
    [InlineData("pic12")]
    [InlineData("pic14")]
    [InlineData("pic18")]
    public void TwoExpansionsStillGetDistinctLabels(string target)
    {
        var (defined, _) = Labels(Emit(target));

        Assert.Equal(2, defined.Count);
        Assert.NotEqual(defined[0], defined[1]);
    }

    private static string Emit(string target) => target switch
    {
        "pic12" => Pic12(TwoExpansions("GOTO")),
        "pic14" => Pic14(TwoExpansions("GOTO")),
        _ => Pic18(TwoExpansions("BRA")),
    };
}
