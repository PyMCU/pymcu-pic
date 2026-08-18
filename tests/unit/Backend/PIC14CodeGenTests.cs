using PyMCU.Backend.Targets.PIC14;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;
using IrBinaryOp = PyMCU.IR.BinaryOp;
using IrUnaryOp = PyMCU.IR.UnaryOp;

namespace PyMCU.UnitTests;

public class PIC14CodeGenTests
{
    private static readonly DeviceConfig Pic16f84a = new() { Chip = "pic16f84a", Arch = "pic14" };

    private static string Compile(ProgramIR program, DeviceConfig? config = null)
    {
        var codegen = new PIC14CodeGen(config ?? Pic16f84a);
        var sw = new StringWriter();
        codegen.Compile(program, sw);
        return sw.ToString();
    }

    private static ProgramIR GenerateIR(string source)
    {
        var lexer = new Lexer(source);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens);
        var ast = parser.ParseProgram();
        var irGen = new IRGenerator();
        return irGen.Generate(ast, new Dictionary<string, ProgramNode>(), Pic16f84a);
    }

    [Fact]
    public void Pic16f628a_EmitsItsConfigWord()
    {
        var prog = MakeProgram("main", new Return(new NoneVal()));
        var asm = Compile(prog, new DeviceConfig { Chip = "pic16f628a", Arch = "pic14" });

        Assert.Contains(
            "\t__CONFIG _FOSC_INTOSCIO & _WDT_OFF & _PWRTE_ON & _MCLRE_ON & " +
            "_BOREN_OFF & _LVP_OFF & _CP_OFF & _CPD_OFF",
            asm);
    }

    [Fact]
    public void ExplicitFuses_StillWinOverTheDefaults()
    {
        var cfg = new DeviceConfig { Chip = "pic16f628a", Arch = "pic14" };
        cfg.Fuses["_FOSC_HS"] = "_FOSC_HS";
        var asm = Compile(MakeProgram("main", new Return(new NoneVal())), cfg);

        Assert.Contains("__CONFIG _FOSC_HS", asm);
        Assert.DoesNotContain("_FOSC_INTOSCIO", asm);
    }

    [Fact]
    public void ChipWithoutADefaultConfigWord_EmitsNoneAtAll()
    {
        var asm = Compile(MakeProgram("main", new Return(new NoneVal())));

        Assert.DoesNotContain("__CONFIG", asm);
    }

    [Fact]
    public void UnhandledExceptionBranch_ParksAtTheTrap_NotAtAnUndefinedSymbol()
    {
        var prog = MakeProgram("main",
            new Call("helper", new List<Val>(), new NoneVal()),
            new BranchOnError("__pymcu_unhandled_exn"),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        Assert.DoesNotContain("GOTO\t__pymcu_unhandled_exn", asm);
        Assert.Contains("GOTO\t__err_trap", asm);
        Assert.Contains("__err_trap", asm);
    }

    [Fact]
    public void TryExceptTrampoline_IsStillRejectedOnPic14()
    {
        var prog = MakeProgram("main",
            new Call("__pymcu_unhandled_exn", new List<Val>(), new NoneVal()),
            new Return(new NoneVal()));

        var ex = Assert.Throws<NotSupportedException>(() => Compile(prog));
        Assert.Contains("runtime exceptions", ex.Message);
    }

    private static ProgramIR MakeProgram(string name, params Instruction[] body)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = name, Body = body.ToList() });
        return prog;
    }

    // ─── SimpleReturn ──────────────────────────────────────────────────────

    [Fact]
    public void SimpleReturn()
    {
        var prog = MakeProgram("main", new Return(new Constant(42)));
        var asm = Compile(prog);

        Assert.Contains("MOVLW\t0x2A", asm);  // 0x2A = 42
        Assert.Contains("RETURN", asm);
        Assert.Contains("main", asm);
    }

    // ─── MultipleFunctions ────────────────────────────────────────────────

    [Fact]
    public void MultipleFunctions()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = "f1", Body = [new Return(new Constant(1))] });
        prog.Functions.Add(new Function { Name = "f2", Body = [new Return(new Constant(2))] });

        var asm = Compile(prog);

        Assert.Contains("f1", asm);
        Assert.Contains("f2", asm);
    }

    // ─── ControlFlow ──────────────────────────────────────────────────────

    [Fact]
    public void ControlFlow()
    {
        var prog = MakeProgram("f",
            new Label("L1"),
            new BitSet(new MemoryAddress(0x05), 0),
            new Jump("L1"));

        var asm = Compile(prog);

        Assert.Contains("GOTO", asm);
        Assert.Contains("L1", asm);
    }

    // ─── UnaryOps ─────────────────────────────────────────────────────────

    [Fact]
    public void UnaryOps()
    {
        var prog = MakeProgram("f",
            new Unary(IrUnaryOp.Neg, new Constant(5), new Variable("x")));

        var asm = Compile(prog);

        Assert.Contains("SUBLW\t0", asm);
    }

    // ─── BinaryOps ────────────────────────────────────────────────────────

    [Fact]
    public void BinaryOps()
    {
        var prog = MakeProgram("f",
            new Binary(IrBinaryOp.Add, new Constant(1), new Constant(2), new Variable("x")));

        var asm = Compile(prog);

        Assert.Contains("ADDLW\t0x02", asm);
    }

    // ─── ComparisonOps ────────────────────────────────────────────────────

    [Fact]
    public void ComparisonOps()
    {
        // x = (1 == 1) — result dst gets 1 if Z flag is set after XORWF.
        // PIC14 pattern: CLRF x; BTFSC STATUS, 2; INCF x, F
        var prog = MakeProgram("f",
            new Binary(IrBinaryOp.Equal, new Constant(1), new Constant(1), new Variable("x")));

        var asm = Compile(prog);

        Assert.Contains("BTFSC\tSTATUS, 2", asm);
    }

    // ─── BitManipulation ──────────────────────────────────────────────────

    [Fact]
    public void BitManipulation()
    {
        var prog = MakeProgram("f",
            new BitSet(new MemoryAddress(0x05), 0),
            new BitClear(new MemoryAddress(0x05), 1));

        var asm = Compile(prog);

        Assert.Contains("BSF\t0x05, 0", asm);
        Assert.Contains("BCF\t0x05, 1", asm);
    }

    // ─── Arguments ────────────────────────────────────────────────────────

    [Fact]
    public void CallWithArguments()
    {
        var ir = GenerateIR(
            "def add(a: int, b: int) -> int:\n    return a + b\n\n" +
            "def main():\n    x: int = add(1, 2)");

        Assert.Equal(2, ir.Functions.Count);
        var addFunc = ir.Functions.First(f => f.Name == "add");
        var mainFunc = ir.Functions.First(f => f.Name == "main");

        Assert.Equal(2, addFunc.Params.Count);
        Assert.Contains("add.a", addFunc.Params[0]);
        Assert.Contains("add.b", addFunc.Params[1]);

        var mainBody = mainFunc.Body;
        Assert.Contains(mainBody, i => i is Copy { Dst: Variable v } && v.Name.Contains("add.a"));
        Assert.Contains(mainBody, i => i is Copy { Dst: Variable v } && v.Name.Contains("add.b"));
        Assert.Contains(mainBody, i => i is Call { FunctionName: "add" });
    }

    [Fact]
    public void StackLayoutWithArguments()
    {
        var ir = GenerateIR(
            "def add(a: int, b: int) -> int:\n    return a + b\n\n" +
            "def main():\n    x: int = add(1, 2)");

        var asm = Compile(ir, new DeviceConfig { Chip = "pic16f84a", Arch = "pic14", Frequency = 4_000_000 });

        Assert.Contains("add.a EQU _stack_base +", asm);
        Assert.Contains("add.b EQU _stack_base +", asm);
        Assert.Contains("main.x EQU _stack_base +", asm);

        Assert.Contains("MOVLW\t0x01", asm);
        Assert.Contains("MOVWF\tadd.a", asm);
        Assert.Contains("MOVLW\t0x02", asm);
        Assert.Contains("MOVWF\tadd.b", asm);
        Assert.Contains("CALL\tadd", asm);
    }

    // ─── Shift semantics ──────────────────────────────────────────────────────

    private static readonly DeviceConfig Pic16f877a =
        new() { Chip = "pic16f877a", Arch = "pic14", RamSize = 368 };

    private static string CompileWide(params Instruction[] body)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = "main", Body = body.ToList() });
        return Compile(prog, Pic16f877a);
    }

    [Fact]
    public void ShiftLeft_ByEight_YieldsZero()
    {
        var asm = CompileWide(new Binary(IrBinaryOp.LShift,
            new Variable("x"), new Constant(8), new Variable("d")));

        Assert.Contains("CLRF\td", asm);
        Assert.DoesNotContain("RLF\td, F", asm);
    }

    [Fact]
    public void ShiftRight_ByEight_OnByte_YieldsZeroInsteadOfReadingPastTheVariable()
    {
        var asm = CompileWide(new Binary(IrBinaryOp.RShift,
            new Variable("x", DataType.UINT8), new Constant(8), new Variable("d")));

        Assert.Contains("CLRF\td", asm);
        Assert.DoesNotContain("x+1", asm);
    }

    [Fact]
    public void ShiftRight_ByEight_OnWord_StillReadsTheHighByte()
    {
        var asm = CompileWide(new Binary(IrBinaryOp.RShift,
            new Variable("x", DataType.UINT16), new Constant(8), new Variable("d")));

        Assert.Contains("MOVF\tx+1, W", asm);
    }

    [Fact]
    public void ShiftRight_Signed_IsArithmetic()
    {
        var asm = CompileWide(new Binary(IrBinaryOp.RShift,
            new Variable("x", DataType.INT8), new Constant(2), new Variable("d", DataType.INT8)));

        Assert.Equal(2, CountOf(asm, "RLF\td, W"));
        Assert.Equal(2, CountOf(asm, "RRF\td, F"));
        Assert.DoesNotContain("BCF\tSTATUS, 0", asm);
    }

    [Fact]
    public void ShiftRight_Signed_ByNine_SaturatesToSignBits()
    {
        var asm = CompileWide(new Binary(IrBinaryOp.RShift,
            new Variable("x", DataType.INT8), new Constant(9), new Variable("d", DataType.INT8)));

        Assert.Equal(7, CountOf(asm, "RRF\td, F"));
        Assert.DoesNotContain("CLRF\td", asm);
    }

    [Fact]
    public void ShiftRight_Unsigned_StaysLogical()
    {
        var asm = CompileWide(new Binary(IrBinaryOp.RShift,
            new Variable("x", DataType.UINT8), new Constant(2), new Variable("d")));

        Assert.Contains("BCF\tSTATUS, 0", asm);
        Assert.DoesNotContain("RLF\td, W", asm);
    }

    [Fact]
    public void VariableShift_Signed_IsArithmetic()
    {
        var asm = CompileWide(new Binary(IrBinaryOp.RShift,
            new Variable("x", DataType.INT8), new Variable("n"), new Variable("d", DataType.INT8)));

        Assert.Contains("RLF\td, W", asm);
    }

    [Fact]
    public void AugAssign_SignedShiftRight_IsArithmetic()
    {
        var asm = CompileWide(new AugAssign(IrBinaryOp.RShift,
            new Variable("x", DataType.INT8), new Constant(1)));

        Assert.Contains("RLF\tx, W", asm);
    }

    // ─── Signed comparisons ───────────────────────────────────────────────────

    [Fact]
    public void SignedJumpIfLessThan_Constant_BiasesBothSides()
    {
        var asm = CompileWide(
            new JumpIfLessThan(new Variable("a", DataType.INT8), new Constant(1), "L"),
            new IR.Label("L"));

        Assert.Contains("XORLW\t0x80", asm);
        Assert.Contains("SUBLW\t0x80", asm);
    }

    [Fact]
    public void UnsignedJumpIfLessThan_Constant_IsUnchanged()
    {
        var asm = CompileWide(
            new JumpIfLessThan(new Variable("a", DataType.UINT8), new Constant(1), "L"),
            new IR.Label("L"));

        Assert.DoesNotContain("XORLW\t0x80", asm);
        Assert.Contains("SUBLW\t0x00", asm);
    }

    [Fact]
    public void SignedCompare_Registers_BiasesThroughCommonRamCell()
    {
        var asm = CompileWide(new Binary(IrBinaryOp.LessThan,
            new Variable("a", DataType.INT8), new Variable("b", DataType.INT8), new Variable("d")));

        Assert.Contains("__s_cmp EQU", asm);
        Assert.Equal(2, CountOf(asm, "XORLW\t0x80"));
        Assert.Contains("SUBWF\t__s_cmp, W", asm);
    }

    [Fact]
    public void SignedCompare_Wide_BiasesOnlyTheHighByte()
    {
        var asm = CompileWide(new Binary(IrBinaryOp.LessThan,
            new Variable("a", DataType.INT16), new Variable("b", DataType.INT16), new Variable("d")));

        Assert.Contains("MOVF\ta+1, W", asm);
        Assert.Equal(2, CountOf(asm, "XORLW\t0x80"));
        Assert.Contains("SUBWF\ta, W", asm);
    }

    [Fact]
    public void MaterializedSignedLessThan_AgainstZero_ComparesAsSigned()
    {
        var asm = CompileWide(new Binary(IrBinaryOp.LessThan,
            new Variable("a", DataType.INT8), new Constant(0), new Variable("d")));

        Assert.Contains("XORLW\t0x80", asm);
        Assert.Contains("SUBLW\t0x7F", asm);
    }

    // ─── Exceptions ───────────────────────────────────────────────────────────

    [Fact]
    public void UnhandledExceptionTrampoline_IsRejectedInsteadOfEmittingAnUndefinedSymbol()
    {
        var ex = Assert.Throws<NotSupportedException>(() => CompileWide(
            new Call("__pymcu_unhandled_exn", new List<Val>(), new NoneVal()),
            new Return(new NoneVal())));

        Assert.Contains("__pymcu_unhandled_exn", ex.Message);
    }

    // ─── Floating point ───────────────────────────────────────────────────────

    [Fact]
    public void TrueDivision_IsRejectedWithAnActionableMessage()
    {
        var ex = Assert.Throws<NotSupportedException>(() => CompileWide(
            new Binary(IrBinaryOp.Div, new Temporary("t", DataType.FLOAT), new Constant(7),
                new Variable("q"))));

        Assert.Contains("true division", ex.Message);
        Assert.Contains("Use '//' for integer division", ex.Message);
    }

    // ─── Wide return ABI ──────────────────────────────────────────────────────

    [Fact]
    public void WideReturn_PassesTheHighBytesThroughTheReturnBlock()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "widen", ReturnType = DataType.UINT16,
            Body = new List<Instruction> { new Return(new Variable("widen.r", DataType.UINT16)) },
        });
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction>
            {
                new Call("widen", new List<Val>(), new Variable("main.w", DataType.UINT16)),
                new Return(new NoneVal()),
            },
        });
        var asm = Compile(prog, Pic16f877a);

        Assert.Contains("__ret_hi EQU", asm);
        Assert.Contains("MOVWF\t__ret_hi", asm);
        Assert.Contains("MOVF\t__ret_hi, W", asm);
        Assert.Contains("MOVWF\tmain.w+1", asm);
    }

    [Fact]
    public void NarrowReturn_KeepsTheEightBitConvention()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "narrow", ReturnType = DataType.UINT8,
            Body = new List<Instruction> { new Return(new Variable("narrow.r")) },
        });
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction>
            {
                new Call("narrow", new List<Val>(), new Variable("main.v")),
                new Return(new NoneVal()),
            },
        });
        var asm = Compile(prog, Pic16f877a);

        Assert.DoesNotContain("__ret_hi", asm);
        Assert.Contains("MOVWF\tmain.v", asm);
    }

    private static int CountOf(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void SmallChipScratch_StaysOutOfItsBadRamWindow()
    {
        var asm = Compile(MakeProgram("main",
            new Binary(PyMCU.IR.BinaryOp.Mul, new Variable("a"), new Variable("b"), new Variable("d")),
            new Return(new NoneVal())),
            new DeviceConfig { Chip = "pic16f84a", Arch = "pic14" });

        // 0x50-0x7F is __BADRAM on the 16F84A: the scratch block must sit below it
        var m = System.Text.RegularExpressions.Regex.Match(asm, @"__m_a EQU 0x([0-9A-F]{2})");
        Assert.True(m.Success, "the mul scratch block must be allocated");
        Assert.True(Convert.ToInt32(m.Groups[1].Value, 16) < 0x50,
            $"scratch at 0x{m.Groups[1].Value} is inside __BADRAM on the 16F84A");
    }

    [Fact]
    public void AddressOutsideTheChipsGprWindow_IsRejected()
    {
        var body = new List<Instruction>();
        for (int k = 0; k < 80; ++k)
            body.Add(new Copy(new Constant(k & 0xFF), new Variable($"v{k}")));
        body.Add(new Return(new NoneVal()));

        var ex = Assert.Throws<InvalidOperationException>(() => Compile(
            MakeProgram("main", body.ToArray()),
            new DeviceConfig { Chip = "pic16f84a", Arch = "pic14" }));

        Assert.Contains("overflows the bank-0 window", ex.Message);
    }

    [Fact]
    public void AllocationBase_FollowsTheChipsFirstGpr()
    {
        var asm84 = Compile(MakeProgram("main",
            new Copy(new Constant(1), new Variable("a")), new Return(new NoneVal())),
            new DeviceConfig { Chip = "pic16f84a", Arch = "pic14" });
        var asm877 = Compile(MakeProgram("main",
            new Copy(new Constant(1), new Variable("a")), new Return(new NoneVal())),
            new DeviceConfig { Chip = "pic16f877a", Arch = "pic14" });

        // the 16F84A's SFRs end at 0x0B; every other PIC14 we support starts at 0x20
        Assert.Contains("CBLOCK 0x0C", asm84);
        Assert.Contains("CBLOCK 0x20", asm877);
    }

    private static DeviceConfig Pic628 => new() { Chip = "pic16f628a", Arch = "pic14" };

    [Fact]
    public void WideAugAssign_WritesEveryByteWithACarryChain()
    {
        var prog = MakeProgram("main",
            new AugAssign(PyMCU.IR.BinaryOp.Sub, new Variable("v", DataType.UINT32), new Variable("d", DataType.UINT32)),
            new Return(new NoneVal()));
        var asm = Compile(prog, Pic628);

        Assert.Equal(4, asm.Split('\n').Count(l => l.Contains("SUBWF\tv")));
        Assert.Contains("SUBWF\tv+3", asm);
        Assert.Contains("INCFSZ", asm);
    }

    [Fact]
    public void WideBinary_GoesThroughTheSameChain_NotTheEightBitPath()
    {
        var prog = MakeProgram("main",
            new Binary(PyMCU.IR.BinaryOp.Sub, new Variable("a", DataType.UINT32), new Variable("b", DataType.UINT32), new Variable("c", DataType.UINT32)),
            new Return(new NoneVal()));
        var asm = Compile(prog, Pic628);

        Assert.Equal(4, asm.Split('\n').Count(l => l.Contains("SUBWF\tc")));
        Assert.Contains("MOVWF\tc+3", asm);
    }

    [Fact]
    public void WideOperationWithNoChain_IsRejectedInsteadOfTruncated()
    {
        var prog = MakeProgram("main",
            new AugAssign(PyMCU.IR.BinaryOp.BitAnd, new Variable("v", DataType.UINT32), new Constant(255)),
            new Return(new NoneVal()));

        var ex = Assert.Throws<NotSupportedException>(() => Compile(prog, Pic628));
        Assert.Contains("4-byte value", ex.Message);
        Assert.Contains("low byte alone", ex.Message);
    }


    [Fact]
    public void WideShiftLeft_RotatesEveryByteLowToHigh()
    {
        var prog = MakeProgram("main",
            new AugAssign(PyMCU.IR.BinaryOp.LShift, new Variable("v", DataType.UINT32), new Constant(1)),
            new Return(new NoneVal()));
        var asm = Compile(prog, Pic628);

        Assert.Equal(4, asm.Split('\n').Count(l => l.Contains("RLF\tv")));
        Assert.Contains("BCF\tSTATUS, 0", asm);
        var lines = asm.Split('\n').Select(l => l.Trim()).ToList();
        Assert.True(lines.FindIndex(l => l.StartsWith("RLF\tv,")) <
                    lines.FindIndex(l => l.StartsWith("RLF\tv+3")),
            "a left shift must rotate from the low byte upward");
    }

    [Fact]
    public void WideShiftRight_RotatesHighToLow_AndSeedsTheSignForSignedValues()
    {
        var unsigned = Compile(MakeProgram("main",
            new AugAssign(PyMCU.IR.BinaryOp.RShift, new Variable("v", DataType.UINT32), new Constant(1)),
            new Return(new NoneVal())), Pic628);
        var signed = Compile(MakeProgram("main",
            new AugAssign(PyMCU.IR.BinaryOp.RShift, new Variable("v", DataType.INT32), new Constant(1)),
            new Return(new NoneVal())), Pic628);

        Assert.Equal(4, unsigned.Split('\n').Count(l => l.Contains("RRF\tv")));
        Assert.Contains("BCF\tSTATUS, 0", unsigned);
        Assert.Contains("RLF\tv+3, W", signed);
        Assert.DoesNotContain("BCF\tSTATUS, 0", signed);
    }
}
