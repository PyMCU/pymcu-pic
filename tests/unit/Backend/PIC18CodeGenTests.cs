using PyMCU.Backend;
using PyMCU.Backend.Targets.PIC18;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

public class PIC18CodeGenTests
{
    private static readonly DeviceConfig Pic18f45k50 = new() { Chip = "pic18f45k50", Arch = "pic18" };

    private static string Compile(ProgramIR program, DeviceConfig? config = null)
    {
        var codegen = new PIC18CodeGen(config ?? Pic18f45k50);
        var sw = new StringWriter();
        codegen.Compile(program, sw);
        return sw.ToString();
    }

    private static ProgramIR MakeProgram(string name, params Instruction[] body)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = name, Body = body.ToList() });
        return prog;
    }

    // ─── SimpleReturn ─────────────────────────────────────────────────────────

    [Fact]
    public void SimpleReturn()
    {
        var prog = MakeProgram("main", new Return(new Constant(42)));
        var asm = Compile(prog);

        Assert.Contains("MOVLW\t0x2A", asm);
        Assert.Contains("RETURN", asm);
        Assert.Contains("p18f45k50.inc", asm);
    }

    // ─── MOVFF_Optimization ───────────────────────────────────────────────────

    [Fact]
    public void MOVFF_Optimization()
    {
        // x = y → MOVFF y, x
        var prog = MakeProgram("main",
            new Copy(new Variable("y"), new Variable("x")),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        Assert.Contains("MOVFF\ty, x", asm);
    }

    // ─── Redundant_MOVFF_Removed ──────────────────────────────────────────────

    [Fact]
    public void Redundant_MOVFF_Removed()
    {
        // x = x — redundant copy, peephole must remove MOVFF x, x
        var prog = MakeProgram("main",
            new Copy(new Variable("x"), new Variable("x")),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        Assert.DoesNotContain("MOVFF\tx, x", asm);
    }

    // ─── ArithmeticAndFactory ─────────────────────────────────────────────────

    [Fact]
    public void ArithmeticAndFactory()
    {
        // (was CodeGenFactory.Create: chip dispatch now lives in PicBackendProvider.)
        var provider = new PyMCU.Backend.Targets.PIC.PicBackendProvider();
        Assert.True(provider.Supports("pic18f45k50"));
        var codegen = provider.Create(Pic18f45k50);
        Assert.IsType<PIC18CodeGen>(codegen);

        // a = 10 + b → MOVLW 0x0A; ADDWF b, W; MOVWF a
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Add, new Constant(10), new Variable("b"), new Variable("a")),
            new Return(new NoneVal()));

        var sw = new StringWriter();
        codegen.Compile(prog, sw);
        var asm = sw.ToString();

        Assert.Contains("MOVLW\t0x0A", asm);
        Assert.Contains("ADDWF\tb, W", asm);
        Assert.Contains("MOVWF\ta", asm);
    }

    // ─── BankedAccess ─────────────────────────────────────────────────────────

    [Fact]
    public void BankedAccess()
    {
        // 200 variables push allocation beyond access bank (0x60) → MOVLB + BANKED suffix
        var body = Enumerable.Range(0, 200)
            .Select(i => (Instruction)new Copy(new Constant(0), new Variable($"v{i}")))
            .ToArray();
        var prog = MakeProgram("main", body);
        var asm = Compile(prog);

        Assert.Contains("MOVLB\t0x01", asm);
        Assert.Contains("MOVWF\tv199, BANKED", asm);
    }

    // ─── SubtractionCorrectness ───────────────────────────────────────────────

    [Fact]
    public void SubtractionCorrectness()
    {
        // a = b - c → MOVF c, W; SUBWF b, W; MOVWF a  (in that order)
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Sub, new Variable("b"), new Variable("c"), new Variable("a")));
        var asm = Compile(prog);

        var posC   = asm.IndexOf("MOVF\tc, W",  StringComparison.Ordinal);
        var posSub = asm.IndexOf("SUBWF\tb, W", StringComparison.Ordinal);
        var posA   = asm.IndexOf("MOVWF\ta",    StringComparison.Ordinal);

        Assert.NotEqual(-1, posC);
        Assert.NotEqual(-1, posSub);
        Assert.NotEqual(-1, posA);
        Assert.True(posC < posSub);
        Assert.True(posSub < posA);
    }

    // ─── Division ─────────────────────────────────────────────────────────────

    [Fact]
    public void Division_UsesTheRestoringRoutine()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.FloorDiv, new Variable("b"), new Variable("c"), new Variable("a")));
        var asm = Compile(prog);

        Assert.Contains("CALL\t__udiv8", asm);
        Assert.Contains("__udiv8_loop", asm);
        Assert.Contains("RLCF\t__d_a, F", asm);
        Assert.Contains("MOVF\t__d_a, W", asm);
    }

    // ─── Shift semantics ──────────────────────────────────────────────────────

    [Fact]
    public void ShiftLeft_ByEight_YieldsZero()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.LShift, new Variable("x"), new Constant(8), new Variable("d")));
        var asm = Compile(prog);

        Assert.Contains("CLRF\tWREG", asm);
        Assert.DoesNotContain("RLCF", asm);
    }

    [Fact]
    public void ShiftRight_ByNine_OnByte_YieldsZero()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.RShift, new Variable("x", DataType.UINT8), new Constant(9), new Variable("d")));
        var asm = Compile(prog);

        Assert.Contains("CLRF\tWREG", asm);
        Assert.DoesNotContain("RRCF", asm);
    }

    [Fact]
    public void ShiftRight_ByEight_OnWord_ReadsHighByte()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.RShift, new Variable("x", DataType.UINT16), new Constant(8), new Variable("d")));
        var asm = Compile(prog);

        Assert.Contains("MOVF\tx+1, W", asm);
        Assert.DoesNotContain("RRCF", asm);
    }

    [Fact]
    public void ShiftRight_Signed_IsArithmetic()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.RShift, new Variable("x", DataType.INT8), new Constant(1), new Variable("d", DataType.INT8)));
        var asm = Compile(prog);

        Assert.Contains("BTFSC\tWREG, 7", asm);
        Assert.Contains("BSF\tSTATUS, C", asm);
        Assert.Equal(1, CountOf(asm, "RRCF"));
    }

    [Fact]
    public void ShiftRight_Signed_ByNine_SaturatesToSignBits()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.RShift, new Variable("x", DataType.INT8), new Constant(9), new Variable("d", DataType.INT8)));
        var asm = Compile(prog);

        Assert.Equal(7, CountOf(asm, "RRCF"));
        Assert.DoesNotContain("CLRF\tWREG", asm);
    }

    [Fact]
    public void ShiftRight_Unsigned_StaysLogical()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.RShift, new Variable("x", DataType.UINT8), new Constant(1), new Variable("d")));
        var asm = Compile(prog);

        Assert.Contains("BCF\tSTATUS, C", asm);
        Assert.DoesNotContain("BTFSC\tWREG, 7", asm);
    }

    [Fact]
    public void VariableShift_ZeroCount_KeepsTheValue()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.RShift, new Variable("x"), new Variable("n"), new Variable("d")));
        var asm = Compile(prog);

        Assert.True(asm.LastIndexOf("MOVF\tx, W", StringComparison.Ordinal)
                    < asm.IndexOf("BZ\t", StringComparison.Ordinal));
    }

    [Fact]
    public void VariableShift_Signed_IsArithmetic()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.RShift, new Variable("x", DataType.INT8), new Variable("n"), new Variable("d", DataType.INT8)));
        var asm = Compile(prog);

        Assert.Contains("BTFSC\tWREG, 7", asm);
    }

    // ─── Signed comparisons ───────────────────────────────────────────────────

    [Fact]
    public void SignedJumpIfLessThan_Constant_BiasesBothSides()
    {
        var prog = MakeProgram("main",
            new JumpIfLessThan(new Variable("a", DataType.INT8), new Constant(1), "L"),
            new Label("L"));
        var asm = Compile(prog);

        Assert.Contains("XORLW\t0x80", asm);
        Assert.Contains("SUBLW\t0x80", asm);
    }

    [Fact]
    public void UnsignedJumpIfLessThan_Constant_IsUnchanged()
    {
        var prog = MakeProgram("main",
            new JumpIfLessThan(new Variable("a", DataType.UINT8), new Constant(1), "L"),
            new Label("L"));
        var asm = Compile(prog);

        Assert.DoesNotContain("XORLW\t0x80", asm);
        Assert.Contains("SUBLW\t0x00", asm);
    }

    [Fact]
    public void SignedJumpIfGreaterOrEqual_NegativeConstant_UsesFastPath()
    {
        var prog = MakeProgram("main",
            new JumpIfGreaterOrEqual(new Variable("a", DataType.INT8), new Constant(-3), "L"),
            new Label("L"));
        var asm = Compile(prog);

        Assert.Contains("XORLW\t0x80", asm);
        Assert.Contains("SUBLW\t0x7C", asm);
    }

    [Fact]
    public void SignedJumpIfLessThan_Registers_BiasesThroughScratchCell()
    {
        var prog = MakeProgram("main",
            new JumpIfLessThan(new Variable("a", DataType.INT8), new Variable("b", DataType.INT8), "L"),
            new Label("L"));
        var asm = Compile(prog);

        Assert.Contains("__signed_cmp EQU 0x000", asm);
        Assert.Equal(2, CountOf(asm, "XORLW\t0x80"));
        Assert.Contains("SUBWF\t__signed_cmp, W", asm);
    }

    [Fact]
    public void MaterializedEqual_DoesNotClobberTheZeroFlag()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Equal, new Variable("a"), new Variable("b"), new Variable("d")));
        var asm = Compile(prog);

        Assert.DoesNotContain("CLRF\tWREG", asm);
        Assert.Contains("MOVLW\t0x00", asm);
    }

    [Fact]
    public void MaterializedLessThan_AgainstZero_StillSetsCarry()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.LessThan, new Variable("a"), new Constant(0), new Variable("d")));
        var asm = Compile(prog);

        Assert.Contains("SUBWF\ta, W", asm);
        Assert.DoesNotContain("MOVF\ta, F", asm);
    }

    [Fact]
    public void MaterializedLessThan_Signed_BiasesBothSides()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.LessThan, new Variable("a", DataType.INT8), new Variable("b", DataType.INT8), new Variable("d")));
        var asm = Compile(prog);

        Assert.Equal(2, CountOf(asm, "XORLW\t0x80"));
        Assert.Contains("SUBWF\t__signed_cmp, W", asm);
    }

    // ─── Scratch allocation ───────────────────────────────────────────────────

    [Fact]
    public void BackendScratchCells_DoNotAliasStackSlots()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.FloorDiv, new Variable("b"), new Variable("c"), new Variable("a")));
        var asm = Compile(prog);

        Assert.Contains("b EQU _stack_base + 0x000", asm);
        Assert.Matches(@"__d_a EQU 0x0[0-5][0-9A-F]", asm);
        Assert.Contains("MOVWF\t__d_a, ACCESS", asm);
    }

    // ─── Call arguments ───────────────────────────────────────────────────────

    [Fact]
    public void Call_LoadsEachArgumentIntoItsParameterSlot()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "f",
            Params = new List<string> { "f.a", "f.b" },
            Body = new List<Instruction> { new Return(new Variable("f.a")) },
        });
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction>
            {
                new Call("f", new List<Val> { new Constant(0x11), new Constant(0x22) }, new NoneVal()),
                new Call("f", new List<Val> { new Constant(0x33), new Constant(0x44) }, new NoneVal()),
            },
        });
        var asm = Compile(prog);

        foreach (var literal in new[] { "0x11", "0x22", "0x33", "0x44" })
            Assert.Contains($"MOVLW\t{literal}", asm);
        Assert.Contains("MOVWF\tf.a", asm);
        Assert.Contains("MOVWF\tf.b", asm);
        Assert.True(asm.IndexOf("MOVLW\t0x22", StringComparison.Ordinal)
                    < asm.IndexOf("CALL\tf", StringComparison.Ordinal));
    }

    // ─── Peephole ─────────────────────────────────────────────────────────────

    [Fact]
    public void Peephole_KeepsTheBranchThatFollowsASkip()
    {
        var prog = MakeProgram("main",
            new Label("loop"),
            new JumpIfBitClear(new Variable("flag"), 0, "done"),
            new Jump("loop"),
            new Label("done"),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        Assert.Contains("BRA\tdone", asm);
        Assert.Contains("BRA\tloop", asm);
    }

    // ─── Interrupt vectors ────────────────────────────────────────────────────

    [Fact]
    public void Interrupt_SitsOnTheHighPriorityVectorAndReturnsWithRetfie()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "isr", IsInterrupt = true,
            Body = new List<Instruction> { new Return(new NoneVal()) },
        });
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction> { new Return(new NoneVal()) },
        });
        var asm = Compile(prog);

        Assert.Contains("ORG 0x0008", asm);
        Assert.Contains("GOTO\tisr", asm);
        Assert.Contains("RETFIE\tFAST", asm);
        Assert.DoesNotContain("isr:\n\tRETURN", asm.Replace("\r\n", "\n"));
    }

    [Fact]
    public void StartStub_SitsAboveBothVectors()
    {
        var prog = MakeProgram("main", new Return(new NoneVal()));
        var asm = Compile(prog);

        Assert.Contains("ORG 0x0008", asm);
        Assert.Contains("ORG 0x0018", asm);
        Assert.Contains("ORG 0x0020", asm);
        Assert.True(asm.IndexOf("ORG 0x0020", StringComparison.Ordinal)
                    < asm.IndexOf("_pymcu_start:", StringComparison.Ordinal));
        Assert.True(asm.IndexOf("_pymcu_start:", StringComparison.Ordinal)
                    < asm.IndexOf("GOTO\tmain", StringComparison.Ordinal));
    }

    [Fact]
    public void Interrupt_WithoutHandler_LeavesARetfieOnTheVector()
    {
        var prog = MakeProgram("main", new Return(new NoneVal()));
        var asm = Compile(prog);
        var afterVector = asm.Substring(asm.IndexOf("ORG 0x0008", StringComparison.Ordinal));

        Assert.StartsWith("ORG 0x0008\n\tRETFIE", afterVector.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Interrupt_TwoHandlers_AreRejected()
    {
        var prog = new ProgramIR();
        foreach (var name in new[] { "isr_a", "isr_b" })
            prog.Functions.Add(new Function
            {
                Name = name, IsInterrupt = true,
                Body = new List<Instruction> { new Return(new NoneVal()) },
            });
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction> { new Return(new NoneVal()) },
        });

        var ex = Assert.Throws<NotSupportedException>(() => Compile(prog));
        Assert.Contains("only one @interrupt handler", ex.Message);
    }

    // ─── Exceptions ───────────────────────────────────────────────────────────

    [Fact]
    public void UnhandledExceptionTrampoline_IsRejectedInsteadOfEmittingAnUndefinedSymbol()
    {
        var prog = MakeProgram("main",
            new Call("__pymcu_unhandled_exn", new List<Val>(), new NoneVal()),
            new Return(new NoneVal()));

        var ex = Assert.Throws<NotSupportedException>(() => Compile(prog));
        Assert.Contains("__pymcu_unhandled_exn", ex.Message);
    }

    [Fact]
    public void Division_ExitsOnBorrowNotOnTheSignBit()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.FloorDiv, new Variable("b"), new Variable("c"), new Variable("a")));
        var asm = Compile(prog);

        Assert.Contains("BNC\t__udiv8_next", asm);
        Assert.DoesNotContain("BN\t", asm);
        Assert.Contains("MOVF\t__d_a, W", asm);
    }

    [Fact]
    public void Modulo_ExitsOnBorrowNotOnTheSignBit()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Mod, new Variable("b"), new Variable("c"), new Variable("a")));
        var asm = Compile(prog);

        Assert.Contains("BNC\t__udiv8_next", asm);
        Assert.DoesNotContain("BN\t", asm);
        Assert.Contains("MOVF\t__d_r, W", asm);
    }

    // ─── Multi-byte integers ──────────────────────────────────────────────────

    private static Variable U16(string n) => new(n, DataType.UINT16);
    private static Variable I16(string n) => new(n, DataType.INT16);

    [Fact]
    public void WideAdd_PropagatesTheCarryIntoTheHighByte()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Add, new Constant(0x1234), new Variable("s"), U16("b")));
        var asm = Compile(prog);

        Assert.Contains("ADDWF\t", asm);
        Assert.Contains("ADDWFC\t", asm);
        Assert.Contains("MOVLW\t0x12", asm);
        Assert.Contains("MOVWF\tb+1", asm);
    }

    [Fact]
    public void WideSub_BorrowsThroughTheHighByte()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Sub, U16("a"), U16("b"), U16("d")));
        var asm = Compile(prog);

        Assert.Contains("SUBWFB\t", asm);
        Assert.Contains("MOVWF\td+1", asm);
    }

    [Fact]
    public void WideCopy_WritesEveryByteOfTheConstant()
    {
        var prog = MakeProgram("main", new Copy(new Constant(0x1234), U16("b")));
        var asm = Compile(prog);

        Assert.Contains("MOVLW\t0x34", asm);
        Assert.Contains("MOVWF\tb,", asm);
        Assert.Contains("MOVLW\t0x12", asm);
        Assert.Contains("MOVWF\tb+1", asm);
    }

    [Fact]
    public void WideCopy_ZeroExtendsAnUnsignedNarrowSource()
    {
        var prog = MakeProgram("main", new Copy(new Variable("s", DataType.UINT8), U16("b")));
        var asm = Compile(prog);

        Assert.Contains("MOVLW\t0x00", asm);
        Assert.Contains("MOVWF\tb+1", asm);
        Assert.DoesNotContain("MOVF\ts+1", asm);
    }

    [Fact]
    public void WideCopy_SignExtendsASignedNarrowSource()
    {
        var prog = MakeProgram("main", new Copy(new Variable("s", DataType.INT8), I16("b")));
        var asm = Compile(prog);

        Assert.Contains("BTFSC\ts, 7", asm);
        Assert.Contains("MOVLW\t0xFF", asm);
        Assert.Contains("MOVWF\tb+1", asm);
    }

    [Fact]
    public void WideCompare_ChainsTheBorrowAcrossBytes()
    {
        var prog = MakeProgram("main",
            new JumpIfGreaterThan(U16("a"), U16("b"), "L"),
            new Label("L"));
        var asm = Compile(prog);

        Assert.Contains("SUBWFB\t", asm);
        Assert.Contains("IORWF\t__mb_acc", asm);
        Assert.DoesNotContain("SUBLW", asm);
    }

    [Fact]
    public void WideCompare_Signed_BiasesOnlyTheTopByte()
    {
        var prog = MakeProgram("main",
            new JumpIfLessThan(I16("a"), I16("b"), "L"),
            new Label("L"));
        var asm = Compile(prog);

        Assert.Equal(2, CountOf(asm, "XORLW\t0x80"));
        Assert.True(asm.IndexOf("MOVF\ta+1, W", StringComparison.Ordinal)
                    < asm.IndexOf("XORLW\t0x80", StringComparison.Ordinal));
    }

    [Fact]
    public void WideShiftRight_ShiftsEveryByte()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.RShift, U16("w"), new Constant(4), U16("d")));
        var asm = Compile(prog);

        Assert.Equal(4, CountOf(asm, "RRCF\td+1"));
        Assert.Equal(4, CountOf(asm, "RRCF\td,"));
    }

    [Fact]
    public void WideShiftRight_Signed_FillsFromTheSignBit()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.RShift, I16("n"), new Constant(2), I16("d")));
        var asm = Compile(prog);

        Assert.Contains("BTFSC\td+1, 7", asm);
        Assert.Contains("BSF\tSTATUS, C", asm);
    }

    [Fact]
    public void WideShiftLeft_ByEight_MovesTheLowByteUp()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.LShift, U16("w"), new Constant(8), U16("d")));
        var asm = Compile(prog);

        Assert.Contains("MOVF\tw, W", asm);
        Assert.Contains("MOVWF\td+1", asm);
        Assert.Contains("MOVLW\t0x00", asm);
        Assert.DoesNotContain("RLCF", asm);
    }

    [Fact]
    public void WideAugAssign_UsesTheCarryChain()
    {
        var prog = MakeProgram("main", new AugAssign(BinaryOp.Add, U16("w"), new Constant(1)));
        var asm = Compile(prog);

        Assert.Contains("ADDWFC\t", asm);
        Assert.Contains("MOVWF\tw+1", asm);
    }

    [Fact]
    public void Call_PassesEveryByteOfAWideArgument()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "f",
            Params = new List<string> { "f.a" },
            Body = new List<Instruction> { new Return(U16("f.a")) },
        });
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction>
            {
                new Call("f", new List<Val> { new Constant(0x1234) }, new NoneVal()),
            },
        });
        var asm = Compile(prog);

        Assert.Contains("MOVLW\t0x12", asm);
        Assert.Contains("MOVWF\tf.a+1", asm);
    }

    [Fact]
    public void WideMul_KeepsBothHalvesOfTheProduct()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Mul, new Variable("a"), new Variable("b"), U16("d")));
        var asm = Compile(prog);

        Assert.Contains("MULWF\tb", asm);
        Assert.Contains("MOVF\tPRODL, W", asm);
        Assert.Contains("MOVF\tPRODH, W", asm);
        Assert.Contains("MOVWF\td+1", asm);
    }

    [Fact]
    public void WideDivision_UsesTheSixteenBitRoutineWithABorrowChain()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.FloorDiv, U16("a"), U16("b"), U16("d")));
        var asm = Compile(prog);

        Assert.Contains("CALL\t__udiv16", asm);
        Assert.Contains("SUBWFB\t__d_r+1, W", asm);
        Assert.Contains("MOVWF\td+1", asm);
    }

    [Fact]
    public void SignedDivision_AppliesThePythonFloorCorrection()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.FloorDiv, new Variable("b", DataType.INT8),
                new Variable("c", DataType.INT8), new Variable("a", DataType.INT8)));
        var asm = Compile(prog);

        Assert.Contains("SETF\t__d_sa", asm);
        Assert.Contains("COMF\t__d_neg, F", asm);
        Assert.Contains("DECF\t__d_a, F", asm);
        Assert.Contains("ADDWF\t__d_r, F", asm);
    }

    [Fact]
    public void WideMul_SixteenBySixteen_SumsThePartialProducts()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Mul, U16("a"), U16("b"), U16("d")));
        var asm = Compile(prog);

        Assert.Equal(3, CountOf(asm, "MULWF"));
        Assert.Contains("MOVFF\tPRODL, __mul_r", asm);
        Assert.Contains("MOVFF\tPRODH, __mul_r+1", asm);
        Assert.Equal(2, CountOf(asm, "ADDWF\t__mul_r+1, F"));
        Assert.Contains("MOVWF\td+1", asm);
    }

    [Fact]
    public void WideMul_BeyondSixteenBits_IsRejectedInsteadOfTruncating()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Mul, U32("a"), U32("b"), U32("d")));

        var ex = Assert.Throws<NotSupportedException>(() => Compile(prog));
        Assert.Contains("wider than 16 bits", ex.Message);
    }

    // ─── 32-bit integers ──────────────────────────────────────────────────────

    private static Variable U32(string n) => new(n, DataType.UINT32);
    private static Variable I32(string n) => new(n, DataType.INT32);

    [Fact]
    public void Wide32Add_PropagatesTheCarryThroughFourBytes()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Add, U32("a"), U32("b"), U32("d")));
        var asm = Compile(prog);

        Assert.Equal(1, CountOf(asm, "ADDWF\t__mb_tmp, W"));
        Assert.Equal(3, CountOf(asm, "ADDWFC\t__mb_tmp, W"));
        foreach (var b in new[] { "d, ", "d+1", "d+2", "d+3" })
            Assert.Contains($"MOVWF\t{b}", asm);
    }

    [Fact]
    public void Wide32Sub_BorrowsThroughFourBytes()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Sub, U32("a"), U32("b"), U32("d")));
        var asm = Compile(prog);

        Assert.Equal(1, CountOf(asm, "SUBWF\t__mb_tmp, W"));
        Assert.Equal(3, CountOf(asm, "SUBWFB\t__mb_tmp, W"));
        Assert.Contains("MOVWF\td+3", asm);
    }

    [Fact]
    public void Wide32Copy_WritesEveryByteOfTheConstant()
    {
        var prog = MakeProgram("main", new Copy(new Constant(0x12345678), U32("d")));
        var asm = Compile(prog);

        foreach (var (literal, slot) in new[]
                 { ("0x78", "d, "), ("0x56", "d+1"), ("0x34", "d+2"), ("0x12", "d+3") })
        {
            Assert.Contains($"MOVLW\t{literal}", asm);
            Assert.Contains($"MOVWF\t{slot}", asm);
        }
    }

    [Fact]
    public void Wide32Copy_SignExtendsANarrowSignedSource()
    {
        var prog = MakeProgram("main", new Copy(new Variable("s", DataType.INT8), I32("d")));
        var asm = Compile(prog);

        Assert.Equal(3, CountOf(asm, "BTFSC\ts, 7"));
        Assert.Equal(3, CountOf(asm, "MOVLW\t0xFF"));
        Assert.Contains("MOVWF\td+3", asm);
    }

    [Fact]
    public void Wide32Compare_ChainsTheBorrowAcrossFourBytes()
    {
        var prog = MakeProgram("main",
            new JumpIfGreaterOrEqual(U32("a"), U32("b"), "L"),
            new Label("L"));
        var asm = Compile(prog);

        Assert.Equal(3, CountOf(asm, "SUBWFB\t__mb_tmp, W"));
        Assert.Equal(3, CountOf(asm, "IORWF\t__mb_acc"));
        Assert.Contains("MOVF\t__mb_acc, F", asm);
    }

    [Fact]
    public void Wide32Compare_Signed_BiasesOnlyTheTopByte()
    {
        var prog = MakeProgram("main",
            new JumpIfLessThan(I32("a"), I32("b"), "L"),
            new Label("L"));
        var asm = Compile(prog);

        Assert.Equal(2, CountOf(asm, "XORLW\t0x80"));
        Assert.True(asm.IndexOf("MOVF\ta+3, W", StringComparison.Ordinal)
                    < asm.IndexOf("XORLW\t0x80", StringComparison.Ordinal));
    }

    [Fact]
    public void Wide32ShiftRight_ShiftsEveryByteFromTheTopDown()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.RShift, U32("w"), new Constant(1), U32("d")));
        var asm = Compile(prog);

        foreach (var b in new[] { "d+3", "d+2", "d+1", "d, " })
            Assert.Equal(1, CountOf(asm, $"RRCF\t{b}"));
        Assert.True(asm.IndexOf("RRCF\td+3", StringComparison.Ordinal)
                    < asm.IndexOf("RRCF\td, ", StringComparison.Ordinal));
    }

    [Fact]
    public void Wide32ShiftRight_Signed_FillsFromTheSignBit()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.RShift, I32("n"), new Constant(1), I32("d")));
        var asm = Compile(prog);

        Assert.Contains("BTFSC\td+3, 7", asm);
        Assert.Contains("BSF\tSTATUS, C", asm);
    }

    [Fact]
    public void Wide32ShiftLeft_BySixteen_MovesTwoBytesUp()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.LShift, U32("w"), new Constant(16), U32("d")));
        var asm = Compile(prog);

        Assert.Contains("MOVF\tw+1, W", asm);
        Assert.Contains("MOVWF\td+3", asm);
        Assert.Contains("MOVF\tw, W", asm);
        Assert.Contains("MOVWF\td+2", asm);
        Assert.Equal(2, CountOf(asm, "MOVLW\t0x00"));
        Assert.DoesNotContain("RLCF", asm);
    }

    // ─── Arrays and pointers ──────────────────────────────────────────────────

    [Fact]
    public void ArrayStore_ConstantIndex_UsesDirectAddressing()
    {
        var prog = MakeProgram("main",
            new ArrayStore("buf", new Constant(2), new Constant(0x33), DataType.UINT8, 4));
        var asm = Compile(prog);

        Assert.Contains("MOVLW\t0x33", asm);
        Assert.Contains("MOVWF\tbuf+2", asm);
        Assert.DoesNotContain("LFSR", asm);
    }

    [Fact]
    public void ArrayStore_ConstantIndexOutOfBounds_IsRejected()
    {
        var prog = MakeProgram("main",
            new ArrayStore("buf", new Constant(9), new Constant(1), DataType.UINT8, 4));

        var ex = Assert.Throws<InvalidOperationException>(() => Compile(prog));
        Assert.Contains("out of bounds", ex.Message);
    }

    [Fact]
    public void ArrayStore_VariableIndex_GoesThroughFsr0()
    {
        var prog = MakeProgram("main",
            new ArrayStore("buf", new Variable("i"), new Constant(0x5A), DataType.UINT8, 4));
        var asm = Compile(prog);

        Assert.Contains("LFSR\t0, buf", asm);
        Assert.Contains("ADDWF\tFSR0L, F", asm);
        Assert.Contains("ADDWFC\tFSR0H, F", asm);
        Assert.Contains("MOVWF\tINDF0", asm);
    }

    [Fact]
    public void ArrayLoad_VariableIndex_ReadsThroughIndf0()
    {
        var prog = MakeProgram("main",
            new ArrayLoad("buf", new Variable("i"), new Variable("d"), DataType.UINT8, 4));
        var asm = Compile(prog);

        Assert.Contains("LFSR\t0, buf", asm);
        Assert.Contains("MOVF\tINDF0, W", asm);
        Assert.Contains("MOVWF\td", asm);
    }

    [Fact]
    public void ArrayStore_WideElements_ScaleTheIndexByTheElementSize()
    {
        var prog = MakeProgram("main",
            new ArrayStore("w", new Variable("i"), new Constant(0x1234), DataType.UINT16, 3));
        var asm = Compile(prog);

        Assert.Equal(2, CountOf(asm, "ADDWF\tFSR0L, F"));
        Assert.Contains("MOVWF\tPOSTINC0", asm);
        Assert.Contains("MOVLW\t0x12", asm);
    }

    [Fact]
    public void LoadIndirect_UsesTheFullSixteenBitPointer()
    {
        var prog = MakeProgram("main",
            new LoadIndirect(U16("p"), new Variable("d")));
        var asm = Compile(prog);

        Assert.Contains("MOVFF\tp, FSR0L", asm);
        Assert.Contains("MOVFF\tp+1, FSR0H", asm);
        Assert.DoesNotContain("CLRF\tFSR0H", asm);
    }

    [Fact]
    public void LoadIndirect_WideElement_ReadsEveryByte()
    {
        var prog = MakeProgram("main",
            new LoadIndirect(U16("p"), U32("d"), DataType.UINT32));
        var asm = Compile(prog);

        Assert.Equal(3, CountOf(asm, "MOVF\tPOSTINC0, W"));
        Assert.Contains("MOVF\tINDF0, W", asm);
        Assert.Contains("MOVWF\td+3", asm);
    }

    [Fact]
    public void BytearrayStore_IndexesOffThePointerSlot()
    {
        var prog = MakeProgram("main",
            new BytearrayStore("self", new Constant(3), new Constant(0)));
        var asm = Compile(prog);

        Assert.Contains("MOVFF\tself, FSR0L", asm);
        Assert.Contains("MOVFF\tself+1, FSR0H", asm);
        Assert.Contains("MOVWF\tINDF0", asm);
    }

    [Fact]
    public void ArrayBase_MaterializesTheLowAndHighHalvesOfTheAddress()
    {
        var prog = MakeProgram("main",
            new ArrayStore("buf", new Constant(0), new Constant(0), DataType.UINT8, 4),
            new Copy(new ArrayBase("buf"), U16("p")));
        var asm = Compile(prog);

        Assert.Contains("MOVLW\tLOW(buf)", asm);
        Assert.Contains("MOVWF\tp,", asm);
        Assert.Contains("MOVLW\tHIGH(buf)", asm);
        Assert.Contains("MOVWF\tp+1", asm);
    }

    [Fact]
    public void FlashTable_IsEmittedAndReadThroughTblrd()
    {
        var prog = MakeProgram("main",
            new FlashData("msg", new List<int> { 0x68, 0x69, 0x00 }),
            new ArrayLoadFlash("msg", new Variable("i"), new Variable("d")));
        var asm = Compile(prog);

        Assert.Contains("__flash_msg:", asm);
        Assert.Contains("DB 0x68, 0x69, 0x00", asm);
        Assert.Contains("MOVLW\tLOW(__flash_msg)", asm);
        Assert.Contains("MOVWF\tTBLPTRL", asm);
        Assert.Contains("TBLRD*", asm);
        Assert.Contains("MOVF\tTABLAT, W", asm);
    }

    // ─── Floating point ───────────────────────────────────────────────────────

    [Fact]
    public void FloatToIntConversion_CallsTheSoftFloatRoutine()
    {
        var prog = MakeProgram("main",
            new Copy(new Variable("f", DataType.FLOAT), new Variable("n", DataType.INT16)));
        var asm = Compile(prog);

        Assert.Contains("CALL\t__fp_fix", asm);
        Assert.Contains("MOVWF\tn+1", asm);
    }

    [Fact]
    public void IntToFloatConversion_CallsTheSoftFloatRoutine()
    {
        var prog = MakeProgram("main",
            new Copy(new Variable("n", DataType.INT16), new Variable("f", DataType.FLOAT)));
        var asm = Compile(prog);

        Assert.Contains("CALL\t__fp_float", asm);
        Assert.Contains("MOVWF\tf+3", asm);
    }

    [Fact]
    public void FloatAdd_CallsTheSoftFloatRoutine()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Add, new Variable("a", DataType.FLOAT),
                new Variable("b", DataType.FLOAT), new Variable("d", DataType.FLOAT)));
        var asm = Compile(prog);

        Assert.Contains("CALL\t__fp_add", asm);
        Assert.Contains("MOVWF\t__fp_b+3", asm);
        Assert.Contains("MOVWF\td+3", asm);
    }

    [Fact]
    public void FloatMultiply_CallsTheSoftFloatRoutine()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Mul, new Variable("a", DataType.FLOAT),
                new Variable("b", DataType.FLOAT), new Variable("d", DataType.FLOAT)));
        var asm = Compile(prog);

        Assert.Contains("CALL\t__fp_mul", asm);
        Assert.Contains("MOVWF\td+3", asm);
    }

    [Fact]
    public void FloatDivide_CallsTheSoftFloatRoutine()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Div, new Variable("a", DataType.FLOAT),
                new Variable("b", DataType.FLOAT), new Variable("d", DataType.FLOAT)));
        var asm = Compile(prog);

        Assert.Contains("CALL\t__fp_div", asm);
        Assert.Contains("MOVWF\td+3", asm);
    }

    [Fact]
    public void TrueDivisionIntoAnInteger_DividesAsFloatThenConverts()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Div, new Temporary("t", DataType.FLOAT), new Constant(7),
                new Variable("q")));
        var asm = Compile(prog);

        Assert.Contains("CALL\t__fp_div", asm);
        Assert.Contains("CALL\t__fp_fix", asm);
        Assert.Contains("MOVWF\tq", asm);
    }

    [Fact]
    public void FloatLiteral_MaterializesItsIeeeBytes()
    {
        var prog = MakeProgram("main",
            new Copy(new FloatConstant(1.5), new Variable("f", DataType.FLOAT)));
        var asm = Compile(prog);

        // 1.5f == 0x3FC00000
        Assert.Contains("MOVLW\t0x00", asm);
        Assert.Contains("MOVLW\t0xC0", asm);
        Assert.Contains("MOVLW\t0x3F", asm);
        Assert.Contains("MOVWF\tf+3", asm);
    }

    [Fact]
    public void FloatComparison_CallsTheSoftFloatRoutine()
    {
        var prog = MakeProgram("main",
            new JumpIfLessThan(new Variable("a", DataType.FLOAT),
                new Variable("b", DataType.FLOAT), "L"),
            new Label("L"));
        var asm = Compile(prog);

        Assert.Contains("MOVWF\t__fp_a", asm);
        Assert.Contains("MOVWF\t__fp_b+3", asm);
        Assert.Contains("CALL\t__fp_cmp", asm);
        Assert.Contains("XORLW\t0xFF", asm);
        Assert.Contains("BZ\tL", asm);
        Assert.Contains("#include \"float.inc\"", asm);
    }

    [Fact]
    public void FlashTable_PacksTwoBytesPerProgramWord()
    {
        var prog = MakeProgram("main",
            new FlashData("t", new List<int> { 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x00 }),
            new ArrayLoadFlash("t", new Variable("i"), new Variable("d")));
        var asm = Compile(prog);

        Assert.Contains("DB 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68", asm);
        Assert.Contains("DB 0x69, 0x00", asm);
        Assert.DoesNotContain("DB 0x61\n", asm.Replace("\r\n", "\n"));
    }

    // ─── Interrupt scratch protection ─────────────────────────────────────────

    private static ProgramIR WithIsr(Function isr, params Function[] others)
    {
        var prog = new ProgramIR();
        foreach (var f in others) prog.Functions.Add(f);
        prog.Functions.Add(isr);
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction> { new Return(new NoneVal()) },
        });
        return prog;
    }

    [Fact]
    public void Interrupt_SavesAndRestoresTheScratchItUses()
    {
        var prog = WithIsr(new Function
        {
            Name = "isr", IsInterrupt = true,
            Body = new List<Instruction>
            {
                new Binary(BinaryOp.Add, U16("t"), new Constant(1), U16("t")),
                new Return(new NoneVal()),
            },
        });
        var asm = Compile(prog);

        Assert.Contains("MOVFF\t__mb_tmp, __mb_tmp_s", asm);
        Assert.Contains("MOVFF\t__mb_tmp_s, __mb_tmp", asm);
        Assert.True(asm.IndexOf("MOVFF\t__mb_tmp, __mb_tmp_s", StringComparison.Ordinal)
                    < asm.IndexOf("MOVWF\t__mb_tmp", StringComparison.Ordinal));
        Assert.True(asm.IndexOf("MOVFF\t__mb_tmp_s, __mb_tmp", StringComparison.Ordinal)
                    < asm.IndexOf("RETFIE\tFAST", StringComparison.Ordinal));
    }

    [Fact]
    public void Interrupt_ProtectsScratchUsedOnlyByASharedCallee()
    {
        var helper = new Function
        {
            Name = "helper",
            Body = new List<Instruction>
            {
                new Binary(BinaryOp.Add, U16("acc"), new Constant(1), U16("acc")),
                new Return(new NoneVal()),
            },
        };
        var isr = new Function
        {
            Name = "isr", IsInterrupt = true,
            Body = new List<Instruction>
            {
                new Call("helper", new List<Val>(), new NoneVal()),
                new Return(new NoneVal()),
            },
        };
        var asm = Compile(WithIsr(isr, helper));

        Assert.Contains("MOVFF\t__mb_tmp, __mb_tmp_s", asm);
        Assert.Contains("MOVFF\t__mb_tmp_s, __mb_tmp", asm);
    }

    [Fact]
    public void Interrupt_WithoutScratchUse_GetsNoPrologue()
    {
        var prog = WithIsr(new Function
        {
            Name = "isr", IsInterrupt = true,
            Body = new List<Instruction>
            {
                new BitSet(new Variable("flag"), 0),
                new Return(new NoneVal()),
            },
        });
        var asm = Compile(prog);

        Assert.DoesNotContain("_s,", asm);
        Assert.DoesNotContain(", __mb_tmp_s", asm);
    }

    // ─── Wide return ABI ──────────────────────────────────────────────────────

    private static ProgramIR CallerOf(Function callee, Val dst)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(callee);
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction>
            {
                new Call(callee.Name, new List<Val>(), dst),
                new Return(new NoneVal()),
            },
        });
        return prog;
    }

    [Fact]
    public void WideReturn_PassesTheHighBytesThroughTheReturnBlock()
    {
        var callee = new Function
        {
            Name = "widen", ReturnType = DataType.UINT16,
            Body = new List<Instruction> { new Return(U16("v")) },
        };
        var asm = Compile(CallerOf(callee, U16("w")));

        Assert.Contains("MOVWF\t__ret_hi", asm);
        Assert.Contains("MOVF\t__ret_hi, W", asm);
        Assert.Contains("MOVWF\tw+1", asm);
    }

    [Fact]
    public void WideReturn_LeavesTheLowByteInWLast()
    {
        var callee = new Function
        {
            Name = "widen", ReturnType = DataType.UINT16,
            Body = new List<Instruction> { new Return(U16("v")) },
        };
        var asm = Compile(CallerOf(callee, U16("w")));

        int high = asm.IndexOf("MOVWF\t__ret_hi", StringComparison.Ordinal);
        int low = asm.IndexOf("MOVF\tv, W", StringComparison.Ordinal);
        int ret = asm.IndexOf("RETURN", StringComparison.Ordinal);

        Assert.True(high < low, "the high byte must be stashed before W is loaded");
        Assert.True(low < ret, "W must hold the low byte at RETURN");
    }

    [Fact]
    public void WideReturn_SignExtendsANarrowValue()
    {
        var callee = new Function
        {
            Name = "widen", ReturnType = DataType.INT16,
            Body = new List<Instruction> { new Return(new Variable("v", DataType.INT8)) },
        };
        var asm = Compile(CallerOf(callee, I16("w")));

        Assert.Contains("BTFSC\tv, 7", asm);
        Assert.Contains("MOVLW\t0xFF", asm);
        Assert.Contains("MOVWF\t__ret_hi", asm);
    }

    [Fact]
    public void NarrowReturn_KeepsTheEightBitConvention()
    {
        var callee = new Function
        {
            Name = "narrow", ReturnType = DataType.UINT8,
            Body = new List<Instruction> { new Return(new Variable("v")) },
        };
        var asm = Compile(CallerOf(callee, new Variable("w")));

        Assert.DoesNotContain("__ret_hi", asm);
        Assert.Contains("MOVF\tv, W", asm);
        Assert.Contains("MOVWF\tw", asm);
    }

    [Fact]
    public void IndirectCall_IsRejectedInsteadOfBeingDropped()
    {
        var prog = MakeProgram("main",
            new IndirectCall(U16("fp"), new List<Val>(), new NoneVal()),
            new Return(new NoneVal()));

        var ex = Assert.Throws<NotSupportedException>(() => Compile(prog));
        Assert.Contains("dropped silently", ex.Message);
    }

    // ─── Error channel (division by zero) ─────────────────────────────────────

    [Fact]
    public void UnhandledError_InMain_ParksInTheErrorTrap()
    {
        var prog = MakeProgram("main",
            new SignalError(new Constant(6)),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        Assert.Contains("MOVLW\t0x06", asm);
        Assert.Contains("MOVWF\t__err", asm);
        Assert.Contains("GOTO\t__err_trap", asm);
        Assert.Contains("__err_trap:", asm);
    }

    [Fact]
    public void SignalError_OutsideMain_PropagatesByReturning()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "helper", CanFail = true,
            Body = new List<Instruction>
            {
                new SignalError(new Constant(6)),
                new Return(new NoneVal()),
            },
        });
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction> { new Return(new NoneVal()) },
        });
        var asm = Compile(prog);

        Assert.Contains("MOVWF\t__err", asm);
        Assert.DoesNotContain("__err_trap", asm);
    }

    [Fact]
    public void SignalError_WithCatchLabel_BranchesLocally()
    {
        var prog = MakeProgram("main",
            new SignalError(new Constant(6), "L_catch"),
            new Label("L_catch"),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        Assert.Contains("BRA\tL_catch", asm);
        Assert.DoesNotContain("__err_trap", asm);
    }

    [Fact]
    public void BranchOnError_TestsTheErrorCell()
    {
        var prog = MakeProgram("main",
            new BranchOnError("L_err"),
            new Label("L_err"),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        Assert.Contains("MOVF\t__err, F", asm);
        Assert.Contains("BNZ\tL_err", asm);
    }

    [Fact]
    public void SignalSuccess_ClearsTheErrorCell()
    {
        var prog = MakeProgram("main",
            new SignalSuccess(),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        Assert.Contains("CLRF\t__err", asm);
    }

    [Fact]
    public void AugAssign_ShiftAndDivide_AreLoweredInsteadOfRejected()
    {
        foreach (var op in new[] { BinaryOp.LShift, BinaryOp.RShift, BinaryOp.FloorDiv,
                                   BinaryOp.Mod, BinaryOp.Mul })
        {
            var prog = MakeProgram("main",
                new AugAssign(op, new Variable("x"), new Constant(2)),
                new Return(new NoneVal()));

            var asm = Compile(prog);
            Assert.Contains("MOVWF\tx", asm);
        }
    }

    // ─── Label integrity ──────────────────────────────────────────────────────

    private static void AssertEveryBranchTargetIsDefined(string asm)
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var referenced = new List<string>();

        foreach (var raw in asm.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.EndsWith(':') && !line.Contains(' '))
            {
                defined.Add(line[..^1]);
                continue;
            }

            foreach (var mnemonic in new[] { "BRA\t", "GOTO\t", "CALL\t", "BZ\t", "BNZ\t", "BC\t", "BNC\t", "BN\t" })
                if (line.StartsWith(mnemonic, StringComparison.Ordinal))
                    referenced.Add(line[mnemonic.Length..].Trim());
        }

        foreach (var target in referenced)
        {
            if (target.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;
            Assert.True(defined.Contains(target),
                $"branch to '{target}' has no definition; emitted labels: {string.Join(", ", defined)}");
        }
    }

    [Fact]
    public void BitWrite_ReferencesTheLabelsItDefines()
    {
        var prog = MakeProgram("main",
            new BitWrite(new MemoryAddress(0xF8C), 2, new Variable("x")),
            new Return(new NoneVal()));

        AssertEveryBranchTargetIsDefined(Compile(prog));
    }

    [Fact]
    public void ControlFlowHeavyProgram_HasNoDanglingBranches()
    {
        var prog = MakeProgram("main",
            new BitWrite(new MemoryAddress(0xF8C), 0, new Variable("a")),
            new Binary(BinaryOp.GreaterThan, new Variable("a"), new Variable("b"), new Variable("c")),
            new Binary(BinaryOp.LessEqual, new Variable("a"), new Variable("b"), new Variable("d")),
            new Binary(BinaryOp.RShift, new Variable("a"), new Variable("n"), new Variable("e")),
            new Binary(BinaryOp.FloorDiv, new Variable("a"), new Variable("b"), new Variable("f")),
            new Return(new NoneVal()));

        AssertEveryBranchTargetIsDefined(Compile(prog));
    }

    [Fact]
    public void Bitcast_CopiesTheBitsInsteadOfVanishing()
    {
        var prog = MakeProgram("main",
            new Bitcast(U16("a"), U16("d")),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        Assert.Contains("MOVWF\td,", asm);
        Assert.Contains("MOVWF\td+1", asm);
    }

    [Fact]
    public void UnhandledInstruction_IsRejectedInsteadOfDropped()
    {
        var prog = MakeProgram("main",
            new GcAlloc(new Constant(4), U16("p")),
            new Return(new NoneVal()));

        var ex = Assert.Throws<NotSupportedException>(() => Compile(prog));
        Assert.Contains("unhandled IR instruction", ex.Message);
        Assert.Contains("GcAlloc", ex.Message);
    }

    private static int CountOf(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}