using PyMCU.Backend.Targets.PIC12;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// pymcu-pic#3. PIC12's instruction dispatch had no `default:`, so every IR instruction it does
/// not implement fell out of the switch and was dropped. The program compiled, shipped, and did
/// not do part of what was written. PIC14 and PIC18 both refuse loudly in the same place.
///
/// The count is fourteen, not the "roughly fifteen" the report estimated: the IR defines 42
/// instructions and this switch named 28 of them.
///
/// THREE of the fourteen must NOT be refused. An expansion marker is metadata for the outliner
/// and the two GC roots are bookkeeping, and PIC18 has carried them as explicit no-ops all
/// along. A blanket default would reject every program containing an inline expansion, so the
/// no-op arms are pinned here as their own test rather than left to be re-derived.
///
/// One test per dropped instruction, not one for the family: adding the arm and probing it with
/// a single instruction leaves the rest of the class uncovered, which is how this defect got
/// here in the first place.
/// </summary>
public class PIC12DroppedInstructionTests
{
    private static readonly DeviceConfig Pic10f200 = new() { Chip = "pic10f200", Arch = "pic12" };

    private static string Compile(params Instruction[] body)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = "main", Body = body.ToList() });
        var sw = new StringWriter();
        new PIC12CodeGen(Pic10f200).Compile(prog, sw);
        return sw.ToString();
    }

    private static string Refusal(Instruction instr)
        => Assert.ThrowsAny<Exception>(() => Compile(instr, new Return(new Constant(0)))).Message;

    // The eleven that must be refused, each named. Two classes, and the class decides the
    // sentence: what the part cannot do, against what nobody has written yet.
    public static TheoryData<string, Instruction, string> Refused() => new()
    {
        // No indirect-addressing register on the baseline. Same reason LoadIndirect and
        // StoreIndirect already refused before this change.
        { "BytearrayLoad",  new BytearrayLoad("p", new Constant(0), new Variable("d")), "not supported" },
        { "BytearrayStore", new BytearrayStore("p", new Constant(0), new Constant(1)),  "not supported" },
        { "FlashLoadPtr",   new FlashLoadPtr(new Variable("p"), new Constant(0), new Variable("d")), "not supported" },

        // A collector with no heap to collect.
        { "GcAlloc",        new GcAlloc(new Constant(4), new Variable("d")),            "not supported" },

        // Could be lowered, and has not been.
        { "ArrayLoadFlash", new ArrayLoadFlash("t", new Constant(0), new Variable("d")), "not implemented" },
        { "Bitcast",        new Bitcast(new Variable("s"), new Variable("d")),           "not implemented" },
        { "BranchOnError",  new BranchOnError("L_err"),                                  "not implemented" },
        { "FlashData",      new FlashData("t", new List<int> { 1, 2 }),                  "not implemented" },
        { "IndirectCall",   new IndirectCall(new Variable("f"), new List<Val>(), new Variable("d")), "not implemented" },
        { "SignalError",    new SignalError(new Constant(1)),                            "not implemented" },
        { "SignalSuccess",  new SignalSuccess(),                                         "not implemented" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void ADroppedInstruction_IsRefusedAndNamed(string name, Instruction instr, string kind)
    {
        var message = Refusal(instr);

        Assert.Contains(name, message);
        Assert.Contains(kind, message);
    }

    // The other side of the same switch. These carry no code on purpose and must keep compiling,
    // because an inline expansion appears in almost every real program.
    public static TheoryData<string, Instruction> NoOps() => new()
    {
        { "InlineExpansionMarker", new InlineExpansionMarker("f", false) },
        { "GcRoot",                new GcRoot(new Variable("v")) },
        { "GcUnroot",              new GcUnroot(new Variable("v")) },
    };

    [Theory]
    [MemberData(nameof(NoOps))]
    public void AnInstructionThatCarriesNoCode_IsNotRefused(string name, Instruction instr)
    {
        var asm = Compile(instr, new Return(new Constant(0)));

        Assert.Contains("RETLW", asm);      // the function after it still compiled
        Assert.DoesNotContain(name, asm);   // and the marker itself emitted nothing
    }

    // The two classes have to read differently, or splitting them bought nothing.
    [Fact]
    public void TheTwoClassesDoNotShareASentence()
    {
        string cannot = Refusal(new GcAlloc(new Constant(4), new Variable("d")));
        string notYet = Refusal(new Bitcast(new Variable("s"), new Variable("d")));

        Assert.Contains("no heap", cannot);
        Assert.DoesNotContain("no heap", notYet);
        Assert.Contains("not implemented", notYet);
        Assert.DoesNotContain("not implemented", cannot);
    }
}
