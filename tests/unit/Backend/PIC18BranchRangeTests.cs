using PyMCU.Backend.Targets.PIC18;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// pymcu-pic#5. Every jump on PIC18 was emitted as `BRA`, whose offset is an 11-bit signed word
/// count, and nothing checked the distance. `asyncio` on pic18f45k50 stopped assembling with
/// three `Error[126] Argument out of range`, one at -1089, because the coroutine dispatch
/// branches over the whole body of the coroutine.
///
/// WHAT THESE ASSERT, and why not the mnemonic at a place: the property gpasm enforces, that
/// every `BRA` in the output can reach the label it names. Pinning "line N says GOTO" would keep
/// passing if the selection stopped selecting and emitted GOTO for everything, which is the
/// other way to be wrong and costs a word on every jump in every program.
///
/// The sibling test InlineAsmLabelRunTests asserts the OTHER half of what gpasm enforces, that
/// every label a jump names is defined. This defect lived inside code that half already covered,
/// so the checker below folds both halves into one answer: an unresolved target is a finding
/// here too, which is also what stops these tests from passing on a program that lost its labels.
///
/// Two things had to be got right for the filler to be filler at all, and both were measured
/// rather than assumed. Dead stores are removed, so the padding writes to an SFR. Code after an
/// unconditional jump is unreachable and removed, so the long body is reached by fall-through or
/// by a conditional, never jumped over. Without either, the "long" function came out at 28
/// instructions and every assertion below passed while testing nothing.
/// </summary>
public class PIC18BranchRangeTests
{
    private static readonly DeviceConfig Pic18f45k50 = new() { Chip = "pic18f45k50", Arch = "pic18" };

    private const int Filler = 1200;      // words, comfortably past BRA's 1023
    private const int MinimumRealised = 1024;

    private static string Compile(List<Instruction> body)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = "main", Body = body });
        var sw = new StringWriter();
        new PIC18CodeGen(Pic18f45k50).Compile(prog, sw);
        return sw.ToString();
    }

    /// <summary>Padding that survives: a store to an SFR is not a dead store.</summary>
    private static IEnumerable<Instruction> Pad(int n)
    {
        for (int i = 0; i < n; i++)
            yield return new Copy(new Constant(i & 0xFF), new MemoryAddress(0xF83));
    }

    private static bool IsTwoWord(string m) => m is "GOTO" or "CALL" or "MOVFF" or "LFSR";

    private static readonly HashSet<string> Conditional =
        new() { "BZ", "BNZ", "BC", "BNC", "BN", "BNN", "BOV", "BNOV" };

    /// <summary>
    /// Walks the emitted text the way the assembler does. Returns every BRA that cannot reach
    /// its label, and every jump naming a label the output does not define. Counting words here
    /// instead of trusting the backend's own arithmetic is the point: if the two disagree, gpasm
    /// sides with this one.
    /// </summary>
    private static List<string> Unreachable(string asm, out int instructions)
    {
        var labelAt = new Dictionary<string, int>();
        var branches = new List<(string Label, int At, string Mnemonic)>();
        instructions = 0;

        int at = 0;
        foreach (var rawLine in asm.Replace("\r\n", "\n").Split('\n'))
        {
            string text = rawLine.Trim();
            int comment = text.IndexOf(';');
            if (comment >= 0) text = text[..comment].Trim();
            if (text.Length == 0) continue;

            if (text.EndsWith(':')) { labelAt[text[..^1]] = at; continue; }

            var parts = text.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string mnemonic = parts[0].ToUpperInvariant();
            if (mnemonic is "LIST" or "CONFIG" or "END" or "#INCLUDE" or "ORG" or "EQU") continue;

            instructions++;
            if (parts.Length > 1 && (mnemonic is "BRA" or "GOTO" || Conditional.Contains(mnemonic)))
                branches.Add((parts[1].Split(',')[0].Trim(), at, mnemonic));

            at += IsTwoWord(mnemonic) ? 2 : 1;
        }

        var bad = new List<string>();
        foreach (var (label, branchAt, mnemonic) in branches)
        {
            if (!labelAt.TryGetValue(label, out int target)) { bad.Add($"{label}:undefined"); continue; }
            if (mnemonic == "GOTO") continue;                       // absolute, always reaches
            int delta = target - (branchAt + 1);
            // BRA carries 11 signed bits; the conditional branches carry 8, a window eight
            // times narrower. Checking them with BRA's numbers would pass a BZ that gpasm
            // rejects, which is the whole defect one level down.
            int lo = mnemonic == "BRA" ? -1024 : -128;
            int hi = mnemonic == "BRA" ? 1023 : 127;
            if (delta < lo || delta > hi) bad.Add($"{mnemonic} {label}:{delta}");
        }
        return bad;
    }

    private static void AssertEveryBranchReaches(string asm)
    {
        var bad = Unreachable(asm, out int instructions);
        Assert.True(instructions > MinimumRealised,
            $"the padding did not survive: only {instructions} instructions, so nothing here is "
            + "far enough away to need the long form and the test would pass either way");
        Assert.Empty(bad);
    }

    // The shape in the report: -1089 words, a coroutine dispatch branching back over its body.
    // The body falls through from the top, so it is reachable and is not deleted.
    [Fact]
    public void ABackwardJumpOverALongBody_CanReachItsLabel()
    {
        var body = new List<Instruction> { new Label("L_back") };
        body.AddRange(Pad(Filler));
        body.Add(new Jump("L_back"));
        body.Add(new Return(new NoneVal()));

        AssertEveryBranchReaches(Compile(body));
    }

    // Forward, the +1062 and +1041 of the report. Two arms, so the long one is reached by the
    // conditional rather than jumped over, which is what keeps it in the output.
    [Fact]
    public void AForwardJumpOverALongBody_CanReachItsLabel()
    {
        var body = new List<Instruction> { new JumpIfZero(new Variable("x"), "L_second") };
        body.AddRange(Pad(20));
        body.Add(new Jump("L_end"));
        body.Add(new Label("L_second"));
        body.AddRange(Pad(Filler));
        body.Add(new Jump("L_end"));
        body.Add(new Label("L_end"));
        body.Add(new Return(new NoneVal()));

        AssertEveryBranchReaches(Compile(body));
    }

    // The other way to be wrong. Emitting GOTO for everything satisfies the two tests above and
    // costs a word on every jump, so the short form is pinned as well: in the two-arm program the
    // SAME label is reached by a near jump and a far one, and the two must not be encoded alike.
    [Fact]
    public void TheSameLabelTakesTheShortFormWhenItIsNear()
    {
        var body = new List<Instruction> { new JumpIfZero(new Variable("x"), "L_second") };
        body.AddRange(Pad(20));
        body.Add(new Jump("L_end"));
        body.Add(new Label("L_second"));
        body.AddRange(Pad(Filler));
        body.Add(new Jump("L_end"));
        body.Add(new Label("L_end"));
        body.Add(new Return(new NoneVal()));

        var asm = Compile(body);

        Assert.Contains("BRA\tL_end", asm);
        Assert.Contains("GOTO\tL_end", asm);
    }

    // A program with no long function must be untouched: this is what makes the fix free on the
    // six corpus programs that already assembled.
    [Fact]
    public void AShortJump_StaysTheOneWordForm()
    {
        var asm = Compile(new List<Instruction>
        {
            new Jump("L_near"),
            new Label("L_near"),
            new Return(new NoneVal()),
        });

        Assert.Contains("BRA\tL_near", asm);
        Assert.DoesNotContain("GOTO\tL_near", asm);
    }

    // ─── the conditional branches, eight times narrower ───────────────────

    // `if x:` over a body of more than about 128 words already failed to assemble with the same
    // Error[126] as the unconditional case, from a BZ rather than a BRA. Reachable from ordinary
    // Python, not latent. The body falls through when the test is false, so it is not deleted.
    [Fact]
    public void AConditionalBranchOverALongBody_CanReachItsLabel()
    {
        var body = new List<Instruction> { new JumpIfZero(new Variable("x"), "L_far") };
        body.AddRange(Pad(Filler));
        body.Add(new Label("L_far"));
        body.Add(new Return(new NoneVal()));

        AssertEveryBranchReaches(Compile(body));
    }

    // There is no long conditional branch on this part, so a far one becomes the OPPOSITE test
    // hopping over a GOTO. The condition has to survive that inversion, which is what this pins:
    // the branch that remains tests the opposite of the one written.
    [Fact]
    public void AFarConditional_BecomesItsInverseHoppingOverAGoto()
    {
        var body = new List<Instruction> { new JumpIfZero(new Variable("x"), "L_far") };
        body.AddRange(Pad(Filler));
        body.Add(new Label("L_far"));
        body.Add(new Return(new NoneVal()));

        var asm = Compile(body);

        Assert.Contains("BNZ", asm);            // the inverse test
        Assert.Contains("GOTO\tL_far", asm);    // and the long jump it hops over
    }

    // The other way to be wrong, again: widening every conditional would satisfy the two above
    // and cost two words on each one. A near conditional keeps its single-word form.
    [Fact]
    public void ANearConditional_StaysTheOneWordForm()
    {
        var body = new List<Instruction> { new JumpIfZero(new Variable("x"), "L_near") };
        body.AddRange(Pad(4));
        body.Add(new Label("L_near"));
        body.Add(new Return(new NoneVal()));

        var asm = Compile(body);

        Assert.Contains("BZ\tL_near", asm);
        Assert.DoesNotContain("GOTO\tL_near", asm);
    }
}
