using PyMCU.Backend.Targets.PIC14;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;
using IrBinaryOp = PyMCU.IR.BinaryOp;

namespace PyMCU.UnitTests;

/// <summary>
/// Software mul/div/mod routines and RAM array access on the PIC14 backend.
/// </summary>
public class PIC14MulDivArrayTests
{
    private static readonly DeviceConfig Pic16f877a =
        new() { Chip = "pic16f877a", TargetChip = "pic16f877a", Arch = "pic14", RamSize = 368 };

    private static readonly DeviceConfig Pic16f84a =
        new() { Chip = "pic16f84a", TargetChip = "pic16f84a", Arch = "pic14", RamSize = 68 };

    private static string Compile(ProgramIR program, DeviceConfig? config = null)
    {
        var codegen = new PIC14CodeGen(config ?? Pic16f877a);
        var sw = new StringWriter();
        codegen.Compile(program, sw);
        return sw.ToString();
    }

    private static ProgramIR MakeProgram(params Instruction[] body)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = "main", Body = body.ToList() });
        return prog;
    }

    // ─── Mul/Div/Mod 8-bit ───────────────────────────────────────────────────

    [Fact]
    public void Mul8_CallsSoftwareRoutine_AndEmitsIt()
    {
        var prog = MakeProgram(
            new Binary(IrBinaryOp.Mul, new Variable("a"), new Variable("b"), new Variable("p")));
        var asm = Compile(prog);

        Assert.Contains("CALL\t__mul8", asm);
        Assert.Contains("__mul8:", asm);
        Assert.Contains("MOVWF\t__m_a", asm);
        Assert.Contains("MOVWF\t__m_b", asm);
        Assert.Contains("MOVF\t__m_r, W", asm); // product comes back from __m_r
        // Scratch lives in common RAM 0x70+ on chips with >= 96 bytes of RAM.
        Assert.Contains("__m_a EQU 0x70", asm);
    }

    [Fact]
    public void Div8_TakesQuotientFromScratchA()
    {
        var prog = MakeProgram(
            new Binary(IrBinaryOp.FloorDiv, new Variable("a"), new Variable("b"), new Variable("q")));
        var asm = Compile(prog);

        Assert.Contains("CALL\t__div8", asm);
        Assert.Contains("__div8:", asm);
        Assert.Contains("MOVF\t__m_a, W", asm); // quotient
        Assert.DoesNotContain("__mul8:", asm);  // only requested routines are emitted
    }

    [Fact]
    public void Mod8_TakesRemainderFromScratchR()
    {
        var prog = MakeProgram(
            new Binary(IrBinaryOp.Mod, new Variable("a"), new Variable("b"), new Variable("r")));
        var asm = Compile(prog);

        Assert.Contains("CALL\t__div8", asm);   // mod shares the divide routine
        Assert.Contains("MOVF\t__m_r, W", asm); // remainder
    }

    [Fact]
    public void Mul16_UsesWideRoutine_WhenDstIs16Bit()
    {
        var prog = MakeProgram(
            new Binary(IrBinaryOp.Mul, new Variable("a"),
                new Constant(251), new Variable("w", DataType.UINT16)));
        var asm = Compile(prog);

        Assert.Contains("CALL\t__mul16", asm);
        Assert.Contains("__mul16:", asm);
        Assert.Contains("MOVWF\tw+1", asm); // high byte of the result is stored
    }

    [Fact]
    public void Div16_SelectedByWideDividend_EvenWithNarrowDst()
    {
        // uint16 // const -> uint8 dst must still run the 16-bit routine.
        var prog = MakeProgram(
            new Binary(IrBinaryOp.FloorDiv, new Variable("w", DataType.UINT16),
                new Constant(300), new Variable("q")));
        var asm = Compile(prog);

        Assert.Contains("CALL\t__div16", asm);
        Assert.Contains("__div16:", asm);
    }

    [Fact]
    public void AugAssign_Mul_LowersToSoftwareRoutine()
    {
        var prog = MakeProgram(
            new AugAssign(IrBinaryOp.Mul, new Variable("x"), new Variable("y")));
        var asm = Compile(prog);

        Assert.Contains("CALL\t__mul8", asm);
    }

    [Fact]
    public void ScratchBlock_MovesBelowRamEnd_OnPic16f84a()
    {
        var prog = MakeProgram(
            new Binary(IrBinaryOp.Mul, new Variable("a"), new Variable("b"), new Variable("p")));
        var asm = Compile(prog, Pic16f84a);

        // 16F84A GPRs end at 0x4F and the block runs to base+9 (__ret_hi), so 0x48
        // put its last cells at 0x50-0x51, inside __BADRAM 0x50-0x7F. Base 0x44 ends at 0x4D.
        Assert.Contains("__m_a EQU 0x44", asm);
    }

    [Fact]
    public void MulDiv_Beyond16Bit_Throws()
    {
        var prog = MakeProgram(
            new Binary(IrBinaryOp.Mul, new Variable("a", DataType.UINT32),
                new Variable("b"), new Variable("p", DataType.UINT32)));

        Assert.Throws<NotSupportedException>(() => Compile(prog));
    }

    // ─── Arrays ──────────────────────────────────────────────────────────────

    [Fact]
    public void ArrayStore_ConstIndex_UsesDirectAddressing()
    {
        var prog = MakeProgram(
            new ArrayStore("main.buf", new Constant(3), new Constant(42), DataType.UINT8, 8));
        var asm = Compile(prog);

        Assert.Contains("main.buf EQU _stack_base", asm);
        Assert.Contains("MOVLW\t0x2A", asm);
        Assert.Contains("MOVWF\tmain.buf+3", asm);
        Assert.DoesNotContain("FSR", asm); // no indirection needed
    }

    [Fact]
    public void ArrayLoad_ConstIndexZero_UsesBareSymbol()
    {
        var prog = MakeProgram(
            new ArrayLoad("main.buf", new Constant(0), new Variable("x"), DataType.UINT8, 8));
        var asm = Compile(prog);

        Assert.Contains("MOVF\tmain.buf, W", asm);
        Assert.Contains("MOVWF\tx", asm);
    }

    [Fact]
    public void ArrayLoad_RuntimeIndex_GoesThroughFsrIndf()
    {
        var prog = MakeProgram(
            new ArrayLoad("main.buf", new Variable("i"), new Variable("x"), DataType.UINT8, 8));
        var asm = Compile(prog);

        Assert.Contains("MOVF\ti, W", asm);
        Assert.Contains("ADDLW\tmain.buf", asm);
        Assert.Contains("MOVWF\tFSR", asm);
        Assert.Contains("BCF\tSTATUS, 7", asm); // IRP forced to bank pair 0/1
        Assert.Contains("MOVF\tINDF, W", asm);
    }

    [Fact]
    public void ArrayStore_RuntimeIndex_WritesThroughIndf()
    {
        var prog = MakeProgram(
            new ArrayStore("main.buf", new Variable("i"), new Variable("v"), DataType.UINT8, 8));
        var asm = Compile(prog);

        Assert.Contains("MOVWF\tFSR", asm);
        Assert.Contains("MOVWF\tINDF", asm);
    }

    [Fact]
    public void ArrayAccess_Uint16Elements_ScalesIndexAndCopiesBothBytes()
    {
        var prog = MakeProgram(
            new ArrayLoad("main.tab", new Variable("i"),
                new Variable("x", DataType.UINT16), DataType.UINT16, 4));
        var asm = Compile(prog);

        Assert.Contains("MOVWF\t__arr_i", asm);
        Assert.Contains("RLF\t__arr_i, F", asm);   // index * 2
        Assert.Contains("INCF\tFSR, F", asm);      // high byte via INDF
        Assert.Contains("MOVWF\tx+1", asm);
    }

    [Fact]
    public void ArrayLoad_ConstIndexOutOfBounds_Throws()
    {
        var prog = MakeProgram(
            new ArrayLoad("main.buf", new Constant(8), new Variable("x"), DataType.UINT8, 8));

        Assert.Throws<InvalidOperationException>(() => Compile(prog));
    }

    [Fact]
    public void RamOverflow_FailsLoudly()
    {
        // 0x20 + 120 > 0x80: the bank-0 bump allocator must reject this.
        var prog = MakeProgram(
            new ArrayStore("main.big", new Constant(0), new Constant(1), DataType.UINT8, 120));

        Assert.Throws<InvalidOperationException>(() => Compile(prog));
    }
}
