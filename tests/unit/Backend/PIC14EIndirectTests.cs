using PyMCU.Backend.Targets.PIC14;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Indirect addressing on the enhanced mid-range core (PIC14E, PIC16F1xxxx):
/// FSR0/INDF0 instead of the legacy FSR/INDF + STATUS&lt;IRP&gt;, with MOVIW/MOVWI
/// for the auto-incrementing steps. The legacy encoding is not merely slower on
/// these parts — "FSR" and "INDF" are not defined in their headers, so it does
/// not assemble at all.
///
/// Every enhanced case is paired with the legacy expectation for the same IR, so
/// the two cores' output cannot drift into each other unnoticed.
/// </summary>
public class PIC14EIndirectTests
{
    private static readonly DeviceConfig Pic16f877a =
        new() { Chip = "pic16f877a", TargetChip = "pic16f877a", Arch = "pic14", RamSize = 368 };

    // PIC16F18877: enhanced mid-range, selected by Arch = "pic14e".
    private static readonly DeviceConfig Pic16f18877 =
        new() { Chip = "pic16f18877", TargetChip = "pic16f18877", Arch = "pic14e", RamSize = 4096 };

    private static string Compile(ProgramIR program, DeviceConfig config)
    {
        var codegen = new PIC14CodeGen(config);
        var sw = new StringWriter();
        codegen.Compile(program, sw);
        return sw.ToString();
    }

    private static string Enhanced(params Instruction[] body) => Compile(MakeProgram(body), Pic16f18877);

    private static string Legacy(params Instruction[] body) => Compile(MakeProgram(body), Pic16f877a);

    private static ProgramIR MakeProgram(params Instruction[] body)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = "main", Body = body.ToList() });
        return prog;
    }

    /// <summary>Instruction lines, tabs collapsed to spaces ("MOVF INDF0, W").</summary>
    private static List<string> Lines(string asm) =>
        asm.Split('\n')
            .Select(l => l.Replace('\t', ' ').Trim())
            .Where(l => l.Length > 0 && !l.StartsWith(';'))
            .ToList();

    /// <summary>Asserts the given instructions appear back to back, in order.</summary>
    private static void AssertRun(string asm, params string[] expected)
    {
        var lines = Lines(asm);
        for (int i = 0; i + expected.Length <= lines.Count; i++)
        {
            bool match = true;
            for (int j = 0; j < expected.Length && match; j++)
                match = lines[i + j] == expected[j];
            if (match) return;
        }

        Assert.Fail($"Expected the run [{string.Join(" | ", expected)}] in:\n{asm}");
    }

    private static void AssertNoLine(string asm, params string[] forbidden)
    {
        var lines = Lines(asm);
        foreach (var line in forbidden)
            Assert.DoesNotContain(line, lines);
    }

    // ─── Arrays, runtime index, 8-bit elements ───────────────────────────────

    [Fact]
    public void ArrayLoad_RuntimeIndex_UsesFsr0AndIndf0()
    {
        var asm = Enhanced(
            new ArrayLoad("main.buf", new Variable("i"), new Variable("x"), DataType.UINT8, 8));

        AssertRun(asm,
            "ADDLW main.buf",
            "MOVWF FSR0L",
            "CLRF FSR0H",     // no implicit high half on this core, unlike IRP
            "MOVF INDF0, W");

        // The legacy encoding must not survive anywhere in the program.
        AssertNoLine(asm, "MOVWF FSR", "MOVF INDF, W", "MOVWF INDF", "BCF STATUS, 7");
    }

    [Fact]
    public void ArrayLoad_RuntimeIndex_LegacyStillUsesFsrIndfAndClearsIrp()
    {
        var asm = Legacy(
            new ArrayLoad("main.buf", new Variable("i"), new Variable("x"), DataType.UINT8, 8));

        AssertRun(asm,
            "ADDLW main.buf",
            "MOVWF FSR",
            "BCF STATUS, 7",
            "MOVF INDF, W");
    }

    [Fact]
    public void ArrayStore_RuntimeIndex_WritesThroughIndf0()
    {
        var asm = Enhanced(
            new ArrayStore("main.buf", new Variable("i"), new Variable("v"), DataType.UINT8, 8));

        AssertRun(asm, "MOVWF FSR0L", "CLRF FSR0H");
        Assert.Contains("MOVWF INDF0", Lines(asm));
        AssertNoLine(asm, "MOVWF INDF", "MOVWI FSR0++"); // 8-bit: nothing to advance past
    }

    // ─── Arrays, runtime index, 16-bit elements ──────────────────────────────

    [Fact]
    public void ArrayLoad_RuntimeIndex_Uint16_FusesTheAdvanceIntoMoviw()
    {
        var asm = Enhanced(
            new ArrayLoad("main.tab", new Variable("i"),
                new Variable("x", DataType.UINT16), DataType.UINT16, 4));

        AssertRun(asm, "RLF __arr_i, F"); // index * 2, shared with the legacy core
        AssertRun(asm,
            "MOVF INDF0, W",
            "MOVWF x",
            "MOVIW ++FSR0",   // advance + high-byte read in one instruction
            "MOVWF x+1");
        AssertNoLine(asm, "INCF FSR, F", "ADDFSR FSR0, 1");
    }

    [Fact]
    public void ArrayLoad_RuntimeIndex_Uint16_LegacyKeepsIncfThenIndf()
    {
        var asm = Legacy(
            new ArrayLoad("main.tab", new Variable("i"),
                new Variable("x", DataType.UINT16), DataType.UINT16, 4));

        AssertRun(asm,
            "MOVF INDF, W",
            "MOVWF x",
            "INCF FSR, F",
            "MOVF INDF, W",
            "MOVWF x+1");
    }

    [Fact]
    public void ArrayStore_RuntimeIndex_Uint16_VariableSource_FusesTheAdvanceIntoMovwi()
    {
        var asm = Enhanced(
            new ArrayStore("main.tab", new Variable("i"),
                new Variable("v", DataType.UINT16), DataType.UINT16, 4));

        AssertRun(asm,
            "MOVF v, W",
            "MOVWI FSR0++",   // low byte written, pointer advanced, one instruction
            "MOVF v+1, W",
            "MOVWF INDF0");
        AssertNoLine(asm, "INCF FSR, F");
    }

    [Fact]
    public void ArrayStore_RuntimeIndex_Uint16_LegacyKeepsIndfThenIncf()
    {
        var asm = Legacy(
            new ArrayStore("main.tab", new Variable("i"),
                new Variable("v", DataType.UINT16), DataType.UINT16, 4));

        AssertRun(asm,
            "MOVWF INDF",
            "INCF FSR, F",
            "MOVF v+1, W",
            "MOVWF INDF");
    }

    [Fact]
    public void ArrayStore_RuntimeIndex_Uint16_ConstantSource_EmitsBothHalves()
    {
        var asm = Enhanced(
            new ArrayStore("main.tab", new Variable("i"), new Constant(0xBEEF), DataType.UINT16, 4));

        AssertRun(asm,
            "MOVLW 0xEF",
            "MOVWI FSR0++",
            "MOVLW 0xBE",
            "MOVWF INDF0");
    }

    [Fact]
    public void ArrayStore_RuntimeIndex_Uint16_ByteSource_ZeroExtendsThroughIndf0()
    {
        var asm = Enhanced(
            new ArrayStore("main.tab", new Variable("i"), new Variable("v"), DataType.UINT16, 4));

        AssertRun(asm, "MOVWI FSR0++", "CLRF INDF0");
    }

    // ─── Pointer dereference (ptr[...] load/store) ───────────────────────────

    [Fact]
    public void LoadIndirect_VariablePointer_UsesFsr0()
    {
        var asm = Enhanced(new LoadIndirect(new Variable("p"), new Variable("x")));

        AssertRun(asm, "MOVF p, W", "MOVWF FSR0L", "CLRF FSR0H", "MOVF INDF0, W");
    }

    [Fact]
    public void StoreIndirect_VariablePointer_UsesFsr0()
    {
        var asm = Enhanced(new StoreIndirect(new Variable("v"), new Variable("p")));

        AssertRun(asm, "MOVF p, W", "MOVWF FSR0L", "CLRF FSR0H");
        Assert.Contains("MOVWF INDF0", Lines(asm));
    }

    [Fact]
    public void LoadIndirect_ConstantPointerAboveOneByte_KeepsTheHighByte()
    {
        // ANSELA on a PIC16F18877 lives at 0x1F38. FSR0 is 16 bits wide, so the
        // address survives intact — the legacy core can only take the low byte.
        var asm = Enhanced(new LoadIndirect(new Constant(0x1F38), new Variable("x")));

        AssertRun(asm,
            "MOVLW 0x38",
            "MOVWF FSR0L",
            "MOVLW 0x1F",
            "MOVWF FSR0H",
            "MOVF INDF0, W");
    }

    [Fact]
    public void LoadIndirect_ConstantPointerBelowOneByte_ClearsTheHighByte()
    {
        var asm = Enhanced(new LoadIndirect(new Constant(0x30), new Variable("x")));

        AssertRun(asm, "MOVLW 0x30", "MOVWF FSR0L", "CLRF FSR0H", "MOVF INDF0, W");
    }

    [Fact]
    public void LoadIndirect_ConstantPointer_LegacyStillTruncatesToEightBits()
    {
        var asm = Legacy(new LoadIndirect(new Constant(0x1F38), new Variable("x")));

        AssertRun(asm, "MOVLW 0x38", "MOVWF FSR", "MOVF INDF, W");
    }

    // ─── Preamble / banking sanity for the enhanced core ─────────────────────

    [Fact]
    public void EnhancedCore_SelectsBanksWithMovlb()
    {
        var asm = Enhanced(
            new ArrayLoad("main.buf", new Variable("i"), new Variable("x"), DataType.UINT8, 8));

        Assert.Contains("MOVLB 0", Lines(asm));
        AssertNoLine(asm, "BSF STATUS, 5", "BCF STATUS, 5", "BSF STATUS, 6", "BCF STATUS, 6");
    }
}
