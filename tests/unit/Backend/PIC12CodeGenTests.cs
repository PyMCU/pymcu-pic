using PyMCU.Backend;
using PyMCU.Backend.Targets.PIC12;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

public class PIC12CodeGenTests
{
    private static readonly DeviceConfig Pic10f200 = new() { Chip = "pic10f200", Arch = "pic12" };

    private static string Compile(ProgramIR program, DeviceConfig? config = null)
    {
        var codegen = new PIC12CodeGen(config ?? Pic10f200);
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

    // ─── SimpleReturn ────────────────────────────────────────────────────────

    [Fact]
    public void SimpleReturn()
    {
        var prog = MakeProgram("main", new Return(new Constant(42)));
        var asm = Compile(prog);

        Assert.Contains("MOVLW\t0x2A", asm);  // 42 = 0x2A
        Assert.Contains("RETLW\t0", asm);
    }

    // ─── NoAddLW_Optimization ────────────────────────────────────────────────

    [Fact]
    public void NoAddLW_Optimization()
    {
        // a = b + 10 — PIC12 baseline has no ADDLW, must use MOVLW + ADDWF
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Add, new Variable("b"), new Constant(10), new Variable("a")),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        Assert.DoesNotContain("ADDLW", asm);
        Assert.Contains("MOVLW\t0x0A", asm);
        Assert.Contains("ADDWF", asm);
    }

    // ─── ProviderSupport ─────────────────────────────────────────────────────
    // (was FactorySupport: CodeGenFactory moved to the monorepo's external-backend
    // guard; chip dispatch now lives in PicBackendProvider.)

    [Fact]
    public void ProviderSupport()
    {
        var provider = new PyMCU.Backend.Targets.PIC.PicBackendProvider();
        Assert.True(provider.Supports("pic10f200"));
        var codegen = provider.Create(Pic10f200);
        Assert.IsType<PIC12CodeGen>(codegen);
    }

    // ─── TRISInstruction ─────────────────────────────────────────────────────

    [Fact]
    public void TRISInstruction()
    {
        // TRISGPIO (0x86) = 0 → TRIS 6 + shadow variable
        var prog = MakeProgram("main",
            new Copy(new Constant(0), new MemoryAddress(0x86)));
        var asm = Compile(prog);

        Assert.Contains("TRIS\t6", asm);
        Assert.Contains("__tris_shadow_6", asm);
    }

    // ─── NegationNoHardcoded ──────────────────────────────────────────────────

    [Fact]
    public void NegationNoHardcoded()
    {
        // x = -y — old code hardcoded MOVWF 0x07 (GPIO), must use __neg_temp instead
        var prog = MakeProgram("main",
            new Unary(UnaryOp.Neg, new Variable("y"), new Variable("x")));
        var asm = Compile(prog);

        Assert.DoesNotContain("MOVWF\t0x07", asm);
        Assert.Contains("__neg_temp", asm);
    }

    // ─── Shift semantics ──────────────────────────────────────────────────────

    [Fact]
    public void ShiftLeft_ByEight_YieldsZero()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.LShift, new Variable("x"), new Constant(8), new Variable("d")));
        var asm = Compile(prog);

        Assert.Contains("CLRF\td", asm);
        Assert.DoesNotContain("RLF\td, F", asm);
    }

    [Fact]
    public void ShiftRight_ByNine_YieldsZero()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.RShift, new Variable("x"), new Constant(9), new Variable("d")));
        var asm = Compile(prog);

        Assert.Contains("CLRF\td", asm);
        Assert.DoesNotContain("RRF\td, F", asm);
    }

    [Fact]
    public void ShiftRight_Signed_IsArithmetic()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.RShift, new Variable("x", DataType.INT8), new Constant(2), new Variable("d", DataType.INT8)));
        var asm = Compile(prog);

        Assert.Contains("RLF\td, W", asm);
        Assert.DoesNotContain("BCF\tSTATUS, 0", asm);
    }

    [Fact]
    public void MaterializedEqual_ClearsTheDestinationBeforeTheSubtract()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Equal, new Variable("a"), new Variable("b"), new Variable("d")));
        var asm = Compile(prog);

        Assert.True(asm.IndexOf("CLRF\td", StringComparison.Ordinal)
                    < asm.IndexOf("SUBWF\ta, W", StringComparison.Ordinal));
    }

    [Fact]
    public void Prologue_NamesTheProcessorAndItsGputilsHeader()
    {
        var asm = Compile(MakeProgram("main", new Return(new NoneVal())));

        Assert.Contains("LIST P=pic10f200", asm);
        Assert.Contains("#include <p10f200.inc>", asm);
        Assert.DoesNotContain("xc.inc", asm);
        Assert.DoesNotContain("PROCESSOR \n", asm);
    }

    [Fact]
    public void Prologue_StartsRamWhereTheChipHasIt()
    {
        var asm = Compile(MakeProgram("main", new Return(new NoneVal())));

        // 0x08-0x0F is BADRAM on the 10F200; its GPRs start at 0x10
        Assert.Contains("_stack_base EQU 0x10", asm);
    }

    [Fact]
    public void DynamicScratch_DoesNotOverlapTheCompiledStack()
    {
        var prog = MakeProgram("main",
            new Copy(new Constant(1), new Variable("a")),
            new Copy(new Constant(2), new Variable("b")),
            new Copy(new Constant(3), new Variable("c")),
            new Copy(new MemoryAddress(0x86, DataType.UINT8), new Variable("d")),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        var shadow = System.Text.RegularExpressions.Regex.Match(asm, @"__tris_shadow_6 EQU 0x([0-9A-F]{2})");
        Assert.True(shadow.Success, "the TRIS shadow must be allocated");
        int shadowAddr = Convert.ToInt32(shadow.Groups[1].Value, 16);
        Assert.True(shadowAddr >= 0x13,
            $"scratch at 0x{shadowAddr:X2} would alias the three stack slots above 0x10");
    }

    [Fact]
    public void ComparisonAgainstALiteral_AvoidsSublw_WhichTheBaselineCoreLacks()
    {
        var prog = MakeProgram("main",
            new JumpIfLessThan(new Variable("n"), new Constant(100), "L"),
            new Label("L"),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        Assert.DoesNotContain("SUBLW", asm);
        Assert.Contains("SUBWF", asm);
        Assert.Contains("__cmp_k", asm);
    }

    [Fact]
    public void EveryEmittedMnemonic_BelongsToTheBaselineInstructionSet()
    {
        var prog = MakeProgram("main",
            new JumpIfLessThan(new Variable("n"), new Constant(100), "L1"),
            new JumpIfGreaterThan(new Variable("n"), new Constant(5), "L1"),
            new JumpIfLessOrEqual(new Variable("n"), new Constant(9), "L1"),
            new JumpIfGreaterOrEqual(new Variable("n"), new Constant(3), "L1"),
            new Binary(BinaryOp.Add, new Variable("n"), new Constant(1), new Variable("n")),
            new Label("L1"),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        var baseline = new HashSet<string>
        {
            "ADDWF", "ANDWF", "CLRF", "CLRW", "COMF", "DECF", "DECFSZ", "INCF", "INCFSZ",
            "IORWF", "MOVF", "MOVWF", "NOP", "RLF", "RRF", "SUBWF", "SWAPF", "XORWF",
            "BCF", "BSF", "BTFSC", "BTFSS", "ANDLW", "CALL", "CLRWDT", "GOTO", "IORLW",
            "MOVLW", "OPTION", "RETLW", "SLEEP", "TRIS", "XORLW",
        };

        foreach (var line in asm.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith(";") || t.EndsWith(":")) continue;
            var mnem = t.Split('\t', ' ')[0].Trim().ToUpperInvariant();
            if (mnem.Length == 0 || t.Contains("EQU") || mnem.StartsWith("#")) continue;
            if (mnem is "LIST" or "ORG" or "END" or "__CONFIG") continue;
            Assert.True(baseline.Contains(mnem),
                $"'{mnem}' is not one of the 33 baseline instructions");
        }
    }

    [Fact]
    public void InlineAsmLabels_AreUniquePerExpansion()
    {
        var prog = MakeProgram("main",
            new InlineAsm("_dly_inner:"),
            new InlineAsm("\tDECFSZ __dly_c1, F"),
            new InlineAsm("\tGOTO _dly_inner"),
            new Copy(new Constant(1), new Variable("x")),
            new InlineAsm("_dly_inner:"),
            new InlineAsm("\tDECFSZ __dly_c1, F"),
            new InlineAsm("\tGOTO _dly_inner"),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        var defs = asm.Split('\n').Where(l => l.TrimEnd().EndsWith(":") && l.Contains("_dly_inner"))
            .Select(l => l.Trim()).ToList();
        Assert.Equal(2, defs.Count);
        Assert.NotEqual(defs[0], defs[1]);
    }

    [Fact]
    public void InlineAsmScratchCells_GetTheirEqu()
    {
        var prog = MakeProgram("main",
            new InlineAsm("\tMOVWF __dly_c1"),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        Assert.Contains("__dly_c1 EQU", asm);
    }
}
