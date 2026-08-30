/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

using PyMCU.Backend.Analysis;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.IR;

namespace PyMCU.Backend.Targets.PIC12;

public class PIC12CodeGen(DeviceConfig cfg) : CodeGen
{
    private readonly List<PIC12AsmLine> _assembly = [];
    private readonly Dictionary<string, int> _symbolTable = new();
    private Dictionary<string, int> _stackLayout = new();
    private int _ramHead = 0x08;
    private int _labelCounter;

    private string MakeLabel(string prefix) => $"{prefix}_{_labelCounter++}";

    private static bool IsSignedValue(Val val) => val switch
    {
        Variable v => v.Type.IsSigned(),
        Temporary t => t.Type.IsSigned(),
        MemoryAddress m => m.Type.IsSigned(),
        Constant c => c.Value < 0,
        _ => false,
    };

    private void Emit(string m) => _assembly.Add(PIC12AsmLine.MakeInstruction(m));
    private void Emit(string m, string o1) => _assembly.Add(PIC12AsmLine.MakeInstruction(m, o1));
    private void Emit(string m, string o1, string o2) => _assembly.Add(PIC12AsmLine.MakeInstruction(m, o1, o2));
    private void EmitLabel(string l) => _assembly.Add(PIC12AsmLine.MakeLabel(l));
    private void EmitComment(string c) => _assembly.Add(PIC12AsmLine.MakeComment(c));
    private void EmitRaw(string t) => _assembly.Add(PIC12AsmLine.MakeRaw(t));

    public override void EmitContextSave()
    {
    }

    public override void EmitContextRestore()
    {
    }

    public override void EmitInterruptReturn()
    {
    }

    private string ResolveAddress(Val val)
    {
        if (val is Constant c) return $"0x{c.Value & 0xFF:X2}";
        if (val is MemoryAddress mem) return $"0x{mem.Address:X2}";
        string name = val switch { Variable v => v.Name, Temporary t => t.Name, _ => "" };
        if (_stackLayout.ContainsKey(name)) return name;
        return GetOrAllocVariable(name);
    }

    private string GetOrAllocVariable(string name)
    {
        if (!_symbolTable.ContainsKey(name))
        {
            _symbolTable[name] = _ramHead++;
            EmitRaw($"{name} EQU 0x{_symbolTable[name]:X2}");
        }

        return name;
    }

    private void LoadIntoW(Val val)
    {
        if (val is Constant c)
        {
            Emit("MOVLW", $"0x{c.Value & 0xFF:X2}");
        }
        else
        {
            string addr = ResolveAddress(val);
            // TRIS registers are write-only on base-line, use shadow
            if (addr == "0x85") addr = GetOrAllocVariable("__tris_shadow_5");
            else if (addr == "0x86") addr = GetOrAllocVariable("__tris_shadow_6");
            else if (addr == "0x87") addr = GetOrAllocVariable("__tris_shadow_7");
            Emit("MOVF", addr, "W");
        }
    }

    private void StoreWInto(Val val)
    {
        string addr = ResolveAddress(val);

        if (addr == "0x81")
        {
            Emit("OPTION");
            return;
        }

        int trisReg = -1;
        if (addr == "0x85") trisReg = 5;
        else if (addr == "0x86") trisReg = 6;
        else if (addr == "0x87") trisReg = 7;

        if (trisReg != -1)
        {
            Emit("TRIS", trisReg.ToString());
            string shadow = GetOrAllocVariable($"__tris_shadow_{trisReg}");
            Emit("MOVWF", shadow);
            return;
        }

        Emit("MOVWF", addr);
    }

    // First GPR and how many, taken from the gputils headers (__BADRAM/__MAXRAM).
    // DeviceConfig.RamSize never reaches this backend -- neither device_info() nor the
    // driver carries it -- so the size lives here until that plumbing exists.
    private static readonly Dictionary<string, (int Base, int Size)> RamMap =
        new(StringComparer.OrdinalIgnoreCase) { ["pic10f200"] = (0x10, 16) };

    private static int StackBaseFor(string chip) =>
        RamMap.TryGetValue(chip, out var r) ? r.Base : 0x08;

    private static int RamSizeFor(string chip, int fromConfig) =>
        RamMap.TryGetValue(chip, out var r) ? r.Size : fromConfig;

    private static readonly System.Text.RegularExpressions.Regex AsmLabelDef =
        new(@"^\s*(_[A-Za-z_][A-Za-z0-9_]*):", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex AsmScratchRef =
        new(@"\b__[A-Za-z_][A-Za-z0-9_]*\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex AsmCodeRef =
        new(@"\b(?:CALL|GOTO)\s+(__[A-Za-z_][A-Za-z0-9_]*)\b",
            System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private int _asmRunCounter;

    /// <summary>
    /// Rewrites every run of <see cref="InlineAsm"/> instructions so that local labels defined
    /// inside the run get a unique per-run suffix, and references within the same run are
    /// renamed with them.
    /// </summary>
    /// <remarks>
    /// A run used to mean ADJACENT InlineAsm instructions. Since PyMCU 0ea529e4 a DebugLine
    /// sits between every <c>asm()</c> of an inlined body, so every run held exactly one
    /// instruction: <c>_dly_inner_12:</c> was renamed inside its own run and the
    /// <c>GOTO _dly_inner_12</c> in the next run kept the old name. gpasm: Error[113] Symbol not
    /// previously defined. The whole PIC corpus stopped assembling, <c>blink</c> on every
    /// chip of all three families.
    /// A DebugLine emits a comment and no code, so it cannot separate two halves of one asm
    /// block. Anything that emits code can, and still ends the run.
    /// </remarks>
    private static bool IsCodeless(Instruction instr) => instr is DebugLine;

    private List<Instruction> UniquifyInlineAsmLabels(List<Instruction> body)
    {
        var result = new List<Instruction>(body.Count);
        int i = 0;
        while (i < body.Count)
        {
            if (body[i] is not InlineAsm) { result.Add(body[i]); i++; continue; }

            // `end` is one past the LAST InlineAsm of the run, so codeless instructions
            // trailing the block are left to the outer loop rather than swallowed here.
            int start = i;
            int end = i + 1;
            for (int j = i; j < body.Count; j++)
            {
                if (body[j] is InlineAsm) { end = j + 1; continue; }
                if (IsCodeless(body[j])) continue;
                break;
            }

            var renames = new Dictionary<string, string>();
            for (int j = start; j < end; j++)
            {
                if (body[j] is not InlineAsm ia) continue;
                var m = AsmLabelDef.Match(ia.Code);
                if (m.Success && !renames.ContainsKey(m.Groups[1].Value))
                    renames[m.Groups[1].Value] = $"{m.Groups[1].Value}_{_asmRunCounter}";
            }
            if (renames.Count > 0) _asmRunCounter++;

            for (int j = start; j < end; j++)
            {
                if (body[j] is not InlineAsm ia) { result.Add(body[j]); continue; }
                var code = ia.Code;
                foreach (var (oldName, newName) in renames)
                    code = System.Text.RegularExpressions.Regex.Replace(
                        code, $@"\b{System.Text.RegularExpressions.Regex.Escape(oldName)}\b", newName);
                result.Add(code == ia.Code ? ia : ia with { Code = code });
            }
            i = end;
        }

        return result;
    }

    private void EmitInlineAsmScratchSymbols(ProgramIR program)
    {
        var definedLabels = new HashSet<string> { "__interrupt" };
        var codeRefs = new HashSet<string>();
        var referenced = new List<string>();

        foreach (var func in program.Functions)
        foreach (var instr in func.Body)
        {
            if (instr is not InlineAsm ia) continue;
            var def = AsmLabelDef.Match(ia.Code);
            if (def.Success) { definedLabels.Add(def.Groups[1].Value); continue; }
            foreach (System.Text.RegularExpressions.Match m in AsmCodeRef.Matches(ia.Code))
                codeRefs.Add(m.Groups[1].Value);
            foreach (System.Text.RegularExpressions.Match m in AsmScratchRef.Matches(ia.Code))
                referenced.Add(m.Value);
        }

        bool any = false;
        foreach (var name in referenced.Distinct())
        {
            if (definedLabels.Contains(name) || codeRefs.Contains(name)) continue;
            if (_stackLayout.ContainsKey(name) || _symbolTable.ContainsKey(name)) continue;
            if (!any) { EmitComment("--- Inline-asm scratch symbols ---"); any = true; }
            _symbolTable[name] = _ramHead++;
            EmitRaw($"{name} EQU 0x{_symbolTable[name]:X2}");
        }
    }

    // The PIC hardware call stack is separate from the GPR file, so every byte of
    // data RAM is available -- but nothing else checks the total. gpasm downgrades
    // an out-of-range EQU to a warning and still writes a hex, so a program that
    // does not fit "builds" and then writes where there is no memory.
    private void RejectRamOverflow(string chip, int stackBase)
    {
        int available = RamSizeFor(chip, cfg.RamSize);
        if (available <= 0) return;
        int used = _ramHead - stackBase;
        if (used <= available) return;
        throw new InvalidOperationException(
            $"PIC12CodeGen: this program needs {used} bytes of data RAM but {chip} has " +
            $"{available} (0x{stackBase:X2}-0x{stackBase + available - 1:X2}). Reduce variables " +
            "or pick a chip with more RAM.");
    }

    private string ChipName()
    {
        if (!string.IsNullOrEmpty(cfg.TargetChip)) return cfg.TargetChip;
        if (!string.IsNullOrEmpty(cfg.Chip)) return cfg.Chip;
        return cfg.DetectedChip;
    }

    public override void Compile(ProgramIR program, TextWriter output)
    {
        _assembly.Clear();

        foreach (var func in program.Functions)
            func.Body = UniquifyInlineAsmLabels(func.Body);

        var allocator = new StackAllocator();
        var (offsets, maxStack) = allocator.Allocate(program);
        _stackLayout = offsets;

        EmitComment("Generated by pymcuc for PIC12 (Base-line)");
        string chipName = ChipName();
        EmitRaw($"\tLIST P={chipName}");
        string chipFile = chipName.ToLowerInvariant();
        if (chipFile.StartsWith("pic")) chipFile = "p" + chipFile.Substring(3);
        EmitRaw($"\t#include <{chipFile}.inc>");

        EmitComment("--- Compiled Stack (Overlays) ---");
        int stackBase = StackBaseFor(chipName);
        _ramHead = stackBase + maxStack;
        EmitRaw($"_stack_base EQU 0x{stackBase:X2}");
        foreach (var (name, offset) in _stackLayout)
            EmitRaw($"{name} EQU _stack_base + {offset}");

        EmitInlineAsmScratchSymbols(program);

        EmitRaw("\tORG 0x00");
        Emit("GOTO", "main");

        foreach (var func in program.Functions)
            CompileFunction(func);

        EmitRaw("\tEND");

        RejectRamOverflow(chipName, stackBase);

        foreach (var line in _assembly)
            output.WriteLine(line.ToString());
    }

    private void CompileFunction(Function func)
    {
        EmitLabel(func.Name);
        foreach (var instr in func.Body)
            CompileInstruction(instr);
    }

    private void CompileInstruction(Instruction instr)
    {
        switch (instr)
        {
            case Return arg: CompileReturn(arg); break;
            case Copy arg: CompileCopy(arg); break;
            case Unary arg: CompileUnary(arg); break;
            case Binary arg: CompileBinary(arg); break;
            case Jump arg: Emit("GOTO", arg.Target); break;
            case JumpIfZero arg: CompileJumpIfZero(arg); break;
            case JumpIfNotZero arg: CompileJumpIfNotZero(arg); break;
            case Label arg: EmitLabel(arg.Name); break;
            case Call arg: CompileCall(arg); break;
            case BitSet arg: CompileBitSet(arg); break;
            case BitClear arg: CompileBitClear(arg); break;
            case BitCheck arg: CompileBitCheck(arg); break;
            case BitWrite arg: CompileBitWrite(arg); break;
            case JumpIfBitSet arg: CompileJumpIfBitSet(arg); break;
            case JumpIfBitClear arg: CompileJumpIfBitClear(arg); break;
            case JumpIfEqual arg: CompileJumpIfEqual(arg); break;
            case JumpIfNotEqual arg: CompileJumpIfNotEqual(arg); break;
            case JumpIfLessThan arg: CompileJumpIfLessThan(arg); break;
            case JumpIfLessOrEqual arg: CompileJumpIfLessOrEqual(arg); break;
            case JumpIfGreaterThan arg: CompileJumpIfGreaterThan(arg); break;
            case JumpIfGreaterOrEqual arg: CompileJumpIfGreaterOrEqual(arg); break;
            case AugAssign arg: CompileAugAssign(arg); break;
            case LoadIndirect:
                throw new NotSupportedException("PIC12: LoadIndirect not supported — PIC12 baseline has no indirect-addressing register");
            case StoreIndirect:
                throw new NotSupportedException("PIC12: StoreIndirect not supported — PIC12 baseline has no indirect-addressing register");
            case InlineAsm arg: _assembly.Add(PIC12AsmLine.MakeRaw(arg.Code)); break;
            case DebugLine arg:
                if (!string.IsNullOrEmpty(arg.SourceFile))
                    EmitComment($"{arg.SourceFile}:{arg.Line}: {arg.Text}");
                else
                    EmitComment($"Line {arg.Line}: {arg.Text}");
                break;
            case ArrayLoad:
                throw new NotSupportedException(
                    "PIC12CodeGen: ArrayLoad (array indexing) is not implemented for the PIC12 baseline backend. " +
                    "The instruction was previously ignored silently, producing wrong code.");
            case ArrayStore:
                throw new NotSupportedException(
                    "PIC12CodeGen: ArrayStore (array element assignment) is not implemented for the PIC12 baseline backend. " +
                    "The instruction was previously ignored silently, producing wrong code.");

            // Carry no code by design, exactly as PIC18 treats them. An expansion marker is
            // metadata for the outliner, and the GC roots are bookkeeping for a collector this
            // backend does not have. They must stay ABOVE the default: a blanket refusal here
            // would reject every program containing an inline expansion, which is most of them.
            case InlineExpansionMarker:
            case GcRoot:
            case GcUnroot:
                break;

            // Everything else. Without this arm the instruction was dropped and the program
            // shipped doing part of what was written; there were fourteen such instructions,
            // and the 10F200 assembles real firmware again, so what falls reaches a chip.
            default:
                throw new NotSupportedException(PIC12Unsupported(instr));
        }
    }

    /// <summary>
    /// Why an instruction has no PIC12 lowering, in the two kinds a reader can act on
    /// differently: one says the part cannot do it, the other says nobody has written it yet.
    /// </summary>
    private static string PIC12Unsupported(Instruction instr) => instr switch
    {
        // The baseline has no FSR/INDF pair, which is the same reason LoadIndirect and
        // StoreIndirect refuse above. Nothing that has to reach memory through a computed
        // address can be lowered here, whoever writes it.
        BytearrayLoad or BytearrayStore or FlashLoadPtr =>
            $"PIC12: {instr.GetType().Name} is not supported on the PIC12 baseline: it needs an "
            + "indirect-addressing register, and this part has none. Index with compile-time "
            + "constants, or pick a PIC14 part.",

        // A collector needs a heap, and the baseline parts have 16 to 96 bytes of RAM in total.
        GcAlloc or GcRoot or GcUnroot =>
            $"PIC12: {instr.GetType().Name} is not supported on the PIC12 baseline: it belongs to "
            + "the garbage collector, and a part with tens of bytes of RAM has no heap to collect. "
            + "Use fixed-size storage.",

        // The rest could be lowered and have not been. Different sentence on purpose: this one
        // is a gap in the backend, not a limit of the silicon.
        _ => $"PIC12: {instr.GetType().Name} is not implemented for the PIC12 baseline backend. "
             + "It was previously dropped without a word, which compiled a program that did not "
             + "do what it said.",
    };

    private void CompileReturn(Return arg)
    {
        LoadIntoW(arg.Value);
        Emit("RETLW", "0"); // PIC12 baseline has no RETURN, use RETLW
    }

    private void CompileCopy(Copy arg)
    {
        LoadIntoW(arg.Src);
        StoreWInto(arg.Dst);
    }

    private void CompileUnary(Unary arg)
    {
        LoadIntoW(arg.Src);
        switch (arg.Op)
        {
            case PyMCU.IR.UnaryOp.BitNot:
                Emit("XORLW", "0xFF");
                break;
            case PyMCU.IR.UnaryOp.Not:
                Emit("XORLW", "1");
                Emit("ANDLW", "1");
                break;
            case PyMCU.IR.UnaryOp.Neg:
                Emit("XORLW", "0xFF");
            {
                string temp = GetOrAllocVariable("__neg_temp");
                Emit("MOVWF", temp);
                Emit("MOVLW", "1");
                Emit("ADDWF", temp, "W");
            }
                break;
        }

        StoreWInto(arg.Dst);
    }

    private void CompileBinary(Binary arg)
    {
        LoadIntoW(arg.Src1);
        string right = ResolveAddress(arg.Src2);

        switch (arg.Op)
        {
            case PyMCU.IR.BinaryOp.Add:
                if (arg.Src2 is Constant)
                {
                    string temp = GetOrAllocVariable("__add_temp");
                    Emit("MOVWF", temp);
                    Emit("MOVLW", right);
                    Emit("ADDWF", temp, "W");
                }
                else Emit("ADDWF", right, "W");

                break;

            case PyMCU.IR.BinaryOp.Sub:
                if (arg.Src2 is Constant)
                {
                    string temp = GetOrAllocVariable("__sub_temp");
                    Emit("MOVWF", temp);
                    Emit("MOVLW", $"(0x100 - ({right})) & 0xFF");
                    Emit("ADDWF", temp, "W");
                }
                else
                {
                    LoadIntoW(arg.Src2);
                    string left = ResolveAddress(arg.Src1);
                    Emit("SUBWF", left, "W");
                }

                break;

            case PyMCU.IR.BinaryOp.BitAnd:
                if (arg.Src2 is Constant) Emit("ANDLW", right);
                else Emit("ANDWF", right, "W");
                break;

            case PyMCU.IR.BinaryOp.BitOr:
                if (arg.Src2 is Constant) Emit("IORLW", right);
                else Emit("IORWF", right, "W");
                break;

            case PyMCU.IR.BinaryOp.BitXor:
                if (arg.Src2 is Constant) Emit("XORLW", right);
                else Emit("XORWF", right, "W");
                break;

            case PyMCU.IR.BinaryOp.LShift:
            case PyMCU.IR.BinaryOp.RShift:
            {
                StoreWInto(arg.Dst);
                string dstAddr = ResolveAddress(arg.Dst);
                string rotateOp = arg.Op == PyMCU.IR.BinaryOp.LShift ? "RLF" : "RRF";
                bool arithmetic = arg.Op == PyMCU.IR.BinaryOp.RShift && IsSignedValue(arg.Src1);

                if (arg.Src2 is Constant c2)
                {
                    int shiftCount = Math.Max(c2.Value, 0);
                    if (arithmetic) shiftCount = Math.Min(shiftCount, 7);
                    if (shiftCount >= 8)
                    {
                        Emit("CLRF", dstAddr);
                        return;
                    }

                    for (int i = 0; i < shiftCount; i++)
                    {
                        if (arithmetic) Emit("RLF", dstAddr, "W");
                        else Emit("BCF", "STATUS", "0");
                        Emit(rotateOp, dstAddr, "F");
                    }
                }
                else
                {
                    string countAddr = ResolveAddress(arg.Src2);
                    string loopLabel = MakeLabel("shift");
                    string doneLabel = MakeLabel("shift_done");
                    Emit("MOVF", countAddr, "W");
                    Emit("BTFSC", "STATUS", "2");
                    Emit("GOTO", doneLabel);
                    EmitLabel(loopLabel);
                    if (arithmetic) Emit("RLF", dstAddr, "W");
                    else Emit("BCF", "STATUS", "0");
                    Emit(rotateOp, dstAddr, "F");
                    Emit("DECFSZ", countAddr, "F");
                    Emit("GOTO", loopLabel);
                    EmitLabel(doneLabel);
                }

                return; // Already stored in dst
            }

            case PyMCU.IR.BinaryOp.Equal:
            case PyMCU.IR.BinaryOp.NotEqual:
            case PyMCU.IR.BinaryOp.LessThan:
            case PyMCU.IR.BinaryOp.LessEqual:
            case PyMCU.IR.BinaryOp.GreaterThan:
            case PyMCU.IR.BinaryOp.GreaterEqual:
            {
                string dstAddr = ResolveAddress(arg.Dst);
                Emit("CLRF", dstAddr);
                LoadIntoW(arg.Src2);
                string left = ResolveAddress(arg.Src1);
                Emit("SUBWF", left, "W");

                switch (arg.Op)
                {
                    case PyMCU.IR.BinaryOp.Equal: Emit("BTFSC", "STATUS", "2"); break;
                    case PyMCU.IR.BinaryOp.NotEqual: Emit("BTFSS", "STATUS", "2"); break;
                    case PyMCU.IR.BinaryOp.LessThan: Emit("BTFSS", "STATUS", "0"); break;
                    case PyMCU.IR.BinaryOp.GreaterEqual: Emit("BTFSC", "STATUS", "0"); break;
                    case PyMCU.IR.BinaryOp.GreaterThan:
                    {
                        string lblSkip = MakeLabel("L_GT");
                        Emit("BTFSS", "STATUS", "0");
                        Emit("GOTO", lblSkip);
                        Emit("BTFSC", "STATUS", "2");
                        Emit("GOTO", lblSkip);
                        Emit("INCF", dstAddr, "F");
                        EmitLabel(lblSkip);
                        return;
                    }
                    case PyMCU.IR.BinaryOp.LessEqual:
                    {
                        var lblSet = MakeLabel("L_LE");
                        var lblSkip = MakeLabel("L_LES");
                        Emit("BTFSS", "STATUS", "0");
                        Emit("GOTO", lblSet);
                        Emit("BTFSS", "STATUS", "2");
                        Emit("GOTO", lblSkip);
                        EmitLabel(lblSet);
                        Emit("INCF", dstAddr, "F");
                        EmitLabel(lblSkip);
                        return;
                    }
                }

                Emit("INCF", dstAddr, "F");
                return;
            }
        }

        StoreWInto(arg.Dst);
    }

    private void CompileJumpIfZero(JumpIfZero arg)
    {
        LoadIntoW(arg.Condition);
        Emit("IORLW", "0");
        Emit("BTFSC", "STATUS", "2");
        Emit("GOTO", arg.Target);
    }

    private void CompileJumpIfNotZero(JumpIfNotZero arg)
    {
        LoadIntoW(arg.Condition);
        Emit("IORLW", "0");
        Emit("BTFSS", "STATUS", "2");
        Emit("GOTO", arg.Target);
    }

    private void CompileCall(Call arg)
    {
        Emit("CALL", arg.FunctionName);
        if (arg.Dst is not NoneVal) StoreWInto(arg.Dst);
    }

    private void CompileBitSet(BitSet arg)
    {
        string addr = ResolveAddress(arg.Target);
        int trisReg = addr == "0x85" ? 5 : addr == "0x86" ? 6 : addr == "0x87" ? 7 : -1;

        if (trisReg != -1)
        {
            string shadow = GetOrAllocVariable($"__tris_shadow_{trisReg}");
            Emit("BSF", shadow, arg.Bit.ToString());
            Emit("MOVF", shadow, "W");
            Emit("TRIS", trisReg.ToString());
        }
        else Emit("BSF", addr, arg.Bit.ToString());
    }

    private void CompileBitClear(BitClear arg)
    {
        string addr = ResolveAddress(arg.Target);
        int trisReg = addr == "0x85" ? 5 : addr == "0x86" ? 6 : addr == "0x87" ? 7 : -1;

        if (trisReg != -1)
        {
            string shadow = GetOrAllocVariable($"__tris_shadow_{trisReg}");
            Emit("BCF", shadow, arg.Bit.ToString());
            Emit("MOVF", shadow, "W");
            Emit("TRIS", trisReg.ToString());
        }
        else Emit("BCF", addr, arg.Bit.ToString());
    }

    private void CompileBitCheck(BitCheck arg)
    {
        string addr = ResolveAddress(arg.Source);
        if (addr == "0x85") addr = GetOrAllocVariable("__tris_shadow_5");
        else if (addr == "0x86") addr = GetOrAllocVariable("__tris_shadow_6");
        else if (addr == "0x87") addr = GetOrAllocVariable("__tris_shadow_7");

        string dst = ResolveAddress(arg.Dst);
        Emit("CLRF", dst);
        Emit("BTFSC", addr, arg.Bit.ToString());
        Emit("INCF", dst, "F");
    }

    private void CompileBitWrite(BitWrite arg)
    {
        string addr = ResolveAddress(arg.Target);
        int trisReg = addr == "0x85" ? 5 : addr == "0x86" ? 6 : addr == "0x87" ? 7 : -1;

        LoadIntoW(arg.Src);
        Emit("IORLW", "0");
        string lblSet = MakeLabel("bit_set");
        string lblEnd = MakeLabel("bit_end");
        Emit("BTFSS", "STATUS", "2");
        Emit("GOTO", lblSet);

        if (trisReg != -1)
        {
            string shadow = GetOrAllocVariable($"__tris_shadow_{trisReg}");
            Emit("BCF", shadow, arg.Bit.ToString());
            Emit("MOVF", shadow, "W");
            Emit("TRIS", trisReg.ToString());
        }
        else Emit("BCF", addr, arg.Bit.ToString());

        Emit("GOTO", lblEnd);
        EmitLabel(lblSet);

        if (trisReg != -1)
        {
            string shadow = GetOrAllocVariable($"__tris_shadow_{trisReg}");
            Emit("BSF", shadow, arg.Bit.ToString());
            Emit("MOVF", shadow, "W");
            Emit("TRIS", trisReg.ToString());
        }
        else Emit("BSF", addr, arg.Bit.ToString());

        EmitLabel(lblEnd);
    }

    private void CompileJumpIfBitSet(JumpIfBitSet arg)
    {
        string addr = ResolveAddress(arg.Source);
        Emit("BTFSC", addr, arg.Bit.ToString());
        Emit("GOTO", arg.Target);
    }

    private void CompileJumpIfBitClear(JumpIfBitClear arg)
    {
        string addr = ResolveAddress(arg.Source);
        Emit("BTFSS", addr, arg.Bit.ToString());
        Emit("GOTO", arg.Target);
    }

    private void CompileJumpIfEqual(JumpIfEqual arg)
    {
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            string src1 = ResolveAddress(arg.Src1);
            Emit("MOVF", src1, "W");
            Emit("XORLW", $"0x{c2.Value:X2}");
            Emit("BTFSC", "STATUS", "2");
            Emit("GOTO", arg.Target);
            return;
        }

        LoadIntoW(arg.Src2);
        string s1 = ResolveAddress(arg.Src1);
        Emit("SUBWF", s1, "W");
        Emit("BTFSC", "STATUS", "2");
        Emit("GOTO", arg.Target);
    }

    private void CompileJumpIfNotEqual(JumpIfNotEqual arg)
    {
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            string src1 = ResolveAddress(arg.Src1);
            Emit("MOVF", src1, "W");
            Emit("XORLW", $"0x{c2.Value:X2}");
            Emit("BTFSS", "STATUS", "2");
            Emit("GOTO", arg.Target);
            return;
        }

        LoadIntoW(arg.Src2);
        string s1 = ResolveAddress(arg.Src1);
        Emit("SUBWF", s1, "W");
        Emit("BTFSS", "STATUS", "2");
        Emit("GOTO", arg.Target);
    }

    // The baseline 12-bit core has no SUBLW: the literal has to travel through a
    // scratch cell so the subtraction happens with SUBWF, which it does have.
    private void EmitLiteralMinusSource(int literal, string sourceAddr)
    {
        string cell = GetOrAllocVariable("__cmp_k");
        Emit("MOVLW", $"0x{literal & 0xFF:X2}");
        Emit("MOVWF", cell);
        Emit("MOVF", sourceAddr, "W");
        Emit("SUBWF", cell, "W");
    }

    private void CompileJumpIfLessThan(JumpIfLessThan arg)
    {
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            int k = c2.Value;
            if (k == 0) return;
            string src1 = ResolveAddress(arg.Src1);
            EmitLiteralMinusSource(k - 1, src1);
            Emit("BTFSC", "STATUS", "0");
            Emit("GOTO", arg.Target);
            return;
        }

        LoadIntoW(arg.Src2);
        string s1 = ResolveAddress(arg.Src1);
        Emit("SUBWF", s1, "W");
        Emit("BTFSS", "STATUS", "0");
        Emit("GOTO", arg.Target);
    }

    private void CompileJumpIfLessOrEqual(JumpIfLessOrEqual arg)
    {
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            string src1 = ResolveAddress(arg.Src1);
            EmitLiteralMinusSource(c2.Value, src1);
            Emit("BTFSC", "STATUS", "0");
            Emit("GOTO", arg.Target);
            return;
        }

        LoadIntoW(arg.Src1);
        string src2Addr = ResolveAddress(arg.Src2);
        Emit("SUBWF", src2Addr, "W");
        Emit("BTFSC", "STATUS", "0");
        Emit("GOTO", arg.Target);
    }

    private void CompileJumpIfGreaterThan(JumpIfGreaterThan arg)
    {
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            string src1 = ResolveAddress(arg.Src1);
            EmitLiteralMinusSource(c2.Value, src1);
            Emit("BTFSS", "STATUS", "0");
            Emit("GOTO", arg.Target);
            return;
        }

        LoadIntoW(arg.Src1);
        string src2Addr = ResolveAddress(arg.Src2);
        Emit("SUBWF", src2Addr, "W");
        Emit("BTFSS", "STATUS", "0");
        Emit("GOTO", arg.Target);
    }

    private void CompileJumpIfGreaterOrEqual(JumpIfGreaterOrEqual arg)
    {
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            int k = c2.Value;
            if (k == 0)
            {
                Emit("GOTO", arg.Target);
                return;
            }

            string src1 = ResolveAddress(arg.Src1);
            EmitLiteralMinusSource(k - 1, src1);
            Emit("BTFSS", "STATUS", "0");
            Emit("GOTO", arg.Target);
            return;
        }

        LoadIntoW(arg.Src2);
        string s1 = ResolveAddress(arg.Src1);
        Emit("SUBWF", s1, "W");
        Emit("BTFSC", "STATUS", "0");
        Emit("GOTO", arg.Target);
    }

    private void CompileAugAssign(AugAssign aa)
    {
        string addr = ResolveAddress(aa.Target);
        LoadIntoW(aa.Operand);
        switch (aa.Op)
        {
            case PyMCU.IR.BinaryOp.Add:    Emit("ADDWF", addr, "F"); break;
            case PyMCU.IR.BinaryOp.Sub:    Emit("SUBWF", addr, "F"); break;
            case PyMCU.IR.BinaryOp.BitAnd: Emit("ANDWF", addr, "F"); break;
            case PyMCU.IR.BinaryOp.BitOr:  Emit("IORWF", addr, "F"); break;
            case PyMCU.IR.BinaryOp.BitXor: Emit("XORWF", addr, "F"); break;
            default: throw new NotSupportedException($"PIC12: AugAssign {aa.Op} not supported on PIC12 baseline");
        }
    }
}