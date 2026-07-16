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

using System.Text.RegularExpressions;
using PyMCU.Backend.Analysis;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.IR;
using IrBinOp = PyMCU.IR.BinaryOp;
using IrUnOp = PyMCU.IR.UnaryOp;

namespace PyMCU.Backend.Targets.PIC14;

public class PIC14CodeGen : CodeGen
{
    // Exposed for ArchStrategy subclasses
    internal readonly DeviceConfig Config;
    private readonly ArchStrategy _strategy;

    private List<PIC14AsmLine> _assembly = new();
    private Dictionary<string, int> _symbolTable = new();
    private Dictionary<string, int> _stackLayout = new();
    private Dictionary<string, int> _varSizes = new();
    private int _ramHead;
    private int _labelCounter;
    private bool _usesFloat;
    private int _currentBank = -1;
    private bool _currentBlockTerminated;
    private string _currentFunctionName = "";

    // --- Software mul/div/mod + array-index scratch -------------------------
    //
    // PIC14 has no hardware multiplier/divider, so Mul/Div/FloorDiv/Mod lower
    // to CALLs into software routines emitted once per program. Their operand
    // cells live in COMMON RAM (0x70-0x7F on most midrange parts, mirrored in
    // every bank) so callers never need a bank select around them. On tiny
    // parts whose GPRs stop before 0x70 (PIC16F84A: 0x0C-0x4F, mirrored in
    // bank 1) the scratch block moves to the top of bank 0 instead — those
    // GPRs are bank-mirrored too, so the "no bank select" property holds.
    private bool _needsMul8, _needsDiv8, _needsMul16, _needsDiv16, _needsArrIdxScale;
    private int _scratchBase;
    private bool NeedsMulDivScratch => _needsMul8 || _needsDiv8 || _needsMul16 || _needsDiv16;
    private string ScratchA => "__m_a";   // 2 bytes: mul multiplicand / div dividend->quotient
    private string ScratchB => "__m_b";   // 2 bytes: mul multiplier / div divisor
    private string ScratchR => "__m_r";   // 2 bytes: mul product / div remainder
    private string ScratchC => "__m_c";   // 1 byte : div bit counter
    private string ScratchIdx => "__arr_i"; // 1 byte: scaled array index (16-bit elements)

    // --- Flash tables (const[str] / const[uint8[N]]) -------------------------
    //
    // Midrange PIC14 program memory is not data-addressable, so flash-resident
    // byte tables lower to RETLW lists reached through a computed GOTO: the
    // caller loads PCLATH with the high byte of the entry address and CALLs the
    // table, whose prologue adds the low byte of the first entry to the index
    // in W (propagating the ADDLW carry into PCLATH) and writes W to PCL. The
    // RETLW at the landing entry returns its literal to the CALLer. Tables are
    // emitted at the END of the program, after the software routines, and every
    // page-sensitive assumption is asserted at assembly time (IF/ERROR).
    private readonly List<(string Name, List<int> Bytes)> _flashData = new();
    private readonly HashSet<string> _flashDataNames = new();
    private bool _needsFlashJmp;
    private Dictionary<string, Function> _funcByName = new();

    private static string FlashLabel(string name) => "__flash_" + name.Replace('.', '_');

    public PIC14CodeGen(DeviceConfig cfg)
    {
        Config = cfg;
        _ramHead = 0x20;
        _labelCounter = 0;

        // Strategy Selection Logic
        // Strict check against config.Arch OR check if TargetChip implies PIC14E
        bool isEnhanced = cfg.Arch == "pic14e" ||
                          (cfg.Arch.StartsWith("pic16f1") && cfg.Arch.Length > 9);

        _strategy = isEnhanced ? new PIC14EStrategy(this) : new PIC14Strategy(this);
    }

    private string MakeLabel(string prefix) => $"{prefix}_{_labelCounter++}";

    // --- Inline-asm hygiene ------------------------------------------------
    //
    // @inline HAL functions (e.g. _delay_ms_pic14) carry raw asm() blocks that
    //   1. define local labels ("_dly_inner:") — duplicated verbatim on every
    //      inline expansion, which gpasm rejects, and
    //   2. reference reserved "__" scratch cells ("__dly_c1") that no IR
    //      instruction ever declares, so nothing allocates them.
    // Both are backend responsibilities: labels are made unique per contiguous
    // InlineAsm run, and "__" data operands get 1-byte allocations after the
    // compiled stack (same dynamic region GetOrAllocVariable uses).

    private static readonly Regex AsmLabelDef =
        new(@"^\s*(_[A-Za-z_][A-Za-z0-9_]*):", RegexOptions.Compiled);
    private static readonly Regex AsmScratchRef =
        new(@"\b__[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.Compiled);
    private static readonly Regex AsmCodeRef =
        new(@"\b(?:CALL|GOTO)\s+(__[A-Za-z_][A-Za-z0-9_]*)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private int _asmRunCounter;

    /// <summary>
    /// Rewrites every maximal run of consecutive <see cref="InlineAsm"/>
    /// instructions so that local labels defined inside the run get a unique
    /// per-run suffix. References within the same run are renamed accordingly.
    /// </summary>
    private List<Instruction> UniquifyInlineAsmLabels(List<Instruction> body)
    {
        var result = new List<Instruction>(body.Count);
        int i = 0;
        while (i < body.Count)
        {
            if (body[i] is not InlineAsm) { result.Add(body[i]); i++; continue; }

            int start = i;
            while (i < body.Count && body[i] is InlineAsm) i++;

            var renames = new Dictionary<string, string>();
            for (int j = start; j < i; j++)
            {
                var m = AsmLabelDef.Match(((InlineAsm)body[j]).Code);
                if (m.Success && !renames.ContainsKey(m.Groups[1].Value))
                    renames[m.Groups[1].Value] = $"{m.Groups[1].Value}_{_asmRunCounter}";
            }
            if (renames.Count > 0) _asmRunCounter++;

            for (int j = start; j < i; j++)
            {
                var ia = (InlineAsm)body[j];
                var code = ia.Code;
                foreach (var (oldName, newName) in renames)
                    code = Regex.Replace(code, $@"\b{Regex.Escape(oldName)}\b", newName);
                result.Add(code == ia.Code ? ia : ia with { Code = code });
            }
        }
        return result;
    }

    /// <summary>
    /// Allocates one byte (after the compiled stack) for every reserved
    /// "__"-prefixed data symbol referenced by inline asm and emits its EQU.
    /// Code symbols (CALL/GOTO targets), asm-defined labels, stack variables
    /// and the float workspace are excluded.
    /// </summary>
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
            foreach (Match m in AsmCodeRef.Matches(ia.Code))
                codeRefs.Add(m.Groups[1].Value);
            foreach (Match m in AsmScratchRef.Matches(ia.Code))
                referenced.Add(m.Value);
        }

        bool any = false;
        foreach (var name in referenced.Distinct())
        {
            if (definedLabels.Contains(name) || codeRefs.Contains(name)) continue;
            if (name.StartsWith("__FP_")) continue; // float workspace lives in the CBLOCK
            if (_stackLayout.ContainsKey(name) || _symbolTable.ContainsKey(name)) continue;
            if (!any) { EmitComment("--- Inline-asm scratch symbols ---"); any = true; }
            _symbolTable[name] = _ramHead++;
            EmitRaw($"{name} EQU 0x{_symbolTable[name]:X2}");
        }
        if (any) EmitRaw("");
    }

    // Parse a hex address string that may or may not have a leading "0x"/"0X" prefix.
    private static int ParseHexAddr(string s)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Convert.ToInt32(s.Substring(2), 16);
        return Convert.ToInt32(s, 16);
    }

    // --- Emission Helpers (internal — used by ArchStrategy) ---

    internal void Emit(string mnemonic) =>
        _assembly.Add(PIC14AsmLine.MakeInstruction(mnemonic));

    internal void Emit(string mnemonic, string op1) =>
        _assembly.Add(PIC14AsmLine.MakeInstruction(mnemonic, op1));

    internal void Emit(string mnemonic, string op1, string op2) =>
        _assembly.Add(PIC14AsmLine.MakeInstruction(mnemonic, op1, op2));

    internal void EmitLabel(string label) =>
        _assembly.Add(PIC14AsmLine.MakeLabel(label));

    internal void EmitComment(string comment) =>
        _assembly.Add(PIC14AsmLine.MakeComment(comment));

    internal void EmitRaw(string text) =>
        _assembly.Add(PIC14AsmLine.MakeRaw(text));

    // --- CodeGen abstract overrides ---

    public override void EmitContextSave() => _strategy.EmitContextSave();
    public override void EmitContextRestore() => _strategy.EmitContextRestore();
    public override void EmitInterruptReturn() => _strategy.EmitInterruptReturn();

    // --- Memory & Bank Management ---

    private string GetOrAllocVariable(string name)
    {
        if (!_symbolTable.ContainsKey(name))
        {
            _symbolTable[name] = _ramHead++;
            EmitRaw($"{name} EQU 0x{_symbolTable[name]:X2}");
        }

        return name;
    }

    private string ResolveAddress(Val val)
    {
        if (val is Constant c)
            return $"0x{c.Value & 0xFF:X2}";
        if (val is MemoryAddress mem)
            return $"0x{mem.Address:X2}";

        string name = val switch
        {
            Variable v => v.Name,
            Temporary t => t.Name,
            _ => ""
        };

        if (_stackLayout.ContainsKey(name))
            return name;

        return GetOrAllocVariable(name);
    }

    private void SelectBank(string operand)
    {
        int addr = -1;

        try
        {
            if (operand.Length > 2 && operand.StartsWith("0x"))
            {
                addr = ParseHexAddr(operand);
            }
            else if (_stackLayout.ContainsKey(operand))
            {
                addr = 0x20 + _stackLayout[operand]; // Assume Bank 0 area
            }
            else if (_symbolTable.ContainsKey(operand))
            {
                addr = _symbolTable[operand];
            }
            else
            {
                // Handle "name+N" suffix (e.g. "nco_init.inc_val+1")
                int plusPos = operand.IndexOf('+');
                if (plusPos >= 0)
                {
                    string baseOp = operand.Substring(0, plusPos);
                    int offset = int.Parse(operand.Substring(plusPos + 1));
                    if (_stackLayout.ContainsKey(baseOp))
                        addr = 0x20 + _stackLayout[baseOp] + offset;
                    else if (_symbolTable.ContainsKey(baseOp))
                        addr = _symbolTable[baseOp] + offset;
                }
            }
        }
        catch
        {
        }

        if (addr != -1)
        {
            int newBank = (addr >> 7) & 0x7F;
            if (_currentBank == newBank) return;
            _strategy.EmitBankSelect(newBank);
            _currentBank = newBank;
        }
        else
        {
            _currentBank = -1; // Invalidate
        }
    }

    // --- Config directives ---

    internal void EmitConfigDirectives()
    {
        if (Config.Fuses == null || Config.Fuses.Count == 0) return;

        string configLine = "\t__CONFIG";
        bool first = true;
        foreach (var kv in Config.Fuses)
        {
            if (!first) configLine += " &";
            if (kv.Value == "ON" || kv.Value == "1" || kv.Value == "TRUE")
                configLine += " " + kv.Key;
            else if (kv.Value == "OFF" || kv.Value == "0" || kv.Value == "FALSE")
                configLine += " " + kv.Key;
            else
                configLine += " " + kv.Value;
            first = false;
        }

        EmitRaw(configLine);
    }

    // --- Main Compile entry point ---

    public override void Compile(ProgramIR program, TextWriter output)
    {
        _assembly.Clear();
        _flashData.Clear();
        _flashDataNames.Clear();
        _needsFlashJmp = false;
        _funcByName = program.Functions.ToDictionary(f => f.Name);

        // Make labels defined inside @inline asm() blocks unique per expansion
        // (a HAL delay loop inlined twice would otherwise duplicate its label).
        foreach (var func in program.Functions)
            func.Body = UniquifyInlineAsmLabels(func.Body);

        var allocator = new StackAllocator();
        var (offsets, totalSize) = allocator.Allocate(program);

        // Pre-scan for floats
        _usesFloat = false;
        foreach (var func in program.Functions)
        {
            foreach (var instr in func.Body)
            {
                if (instr is Binary bin)
                {
                    bool IsFloatVal(Val v) => v switch
                    {
                        Variable vv => vv.Type == DataType.FLOAT,
                        Temporary tt => tt.Type == DataType.FLOAT,
                        _ => false
                    };

                    if (IsFloatVal(bin.Dst) || IsFloatVal(bin.Src1) || IsFloatVal(bin.Src2))
                    {
                        _usesFloat = true;
                        break;
                    }
                }
            }

            if (_usesFloat) break;
        }

        _stackLayout = offsets;
        _varSizes = allocator.VariableSizes;

        // Common-RAM scratch window for the software mul/div routines and the
        // array-index scaler. See the field comments for the placement rules.
        _scratchBase = Config.RamSize > 0 && Config.RamSize < 96 ? 0x48 : 0x70;
        ScanForSoftwareRoutines(program);

        // Advance ram_head past the stack (and float workspace) to prevent
        // collisions with dynamically-allocated variables (delay counters, etc.)
        _ramHead = 0x20 + totalSize;
        if (_usesFloat) _ramHead += 12; // __FP_A(4) + __FP_B(4) + __FP_C(4)

        // 1. Preamble FIRST (LIST, #include, errorlevel, CONFIG)
        _strategy.EmitPreamble();
        EmitRaw("");

        // 2. Stack layout (CBLOCK/ENDC)
        if (_stackLayout.Count > 0 || _usesFloat)
        {
            EmitComment("--- Compiled Stack (Overlays) ---");
            EmitRaw("\tCBLOCK 0x20");
            EmitRaw($"_stack_base: {totalSize}");
            if (_usesFloat)
            {
                EmitRaw("__FP_A: 4");
                EmitRaw("__FP_B: 4");
                EmitRaw("__FP_C: 4");
            }

            EmitRaw("\tENDC");
            EmitRaw("");
        }

        // 3. Variable offsets (EQU)
        if (_stackLayout.Count > 0)
        {
            EmitComment("--- Variable Offsets ---");
            foreach (var kv in _stackLayout)
                EmitRaw($"{kv.Key} EQU _stack_base + {kv.Value}");
            EmitRaw("");
        }

        // 3.4 Common-RAM scratch cells for software mul/div and array indexing
        EmitScratchSymbols();

        // 3.5 Scratch cells referenced by inline asm ("__dly_c1" etc.)
        EmitInlineAsmScratchSymbols(program);

        // 4. Reset vector
        int resetAddr = Config.ResetVector >= 0 ? Config.ResetVector : 0x0000;
        EmitComment("--- Reset Vector ---");
        EmitRaw($"\tORG 0x{resetAddr:X4}");
        Emit("GOTO", "main");
        EmitRaw("");

        // 5. Interrupt vector (PIC14 only supports 0x04)
        int intAddr = Config.InterruptVector >= 0 ? Config.InterruptVector : 0x0004;

        Function? isr0x04 = null;
        foreach (var func in program.Functions)
        {
            if (func.IsInterrupt)
            {
                if (func.InterruptVector != 0x04 && func.InterruptVector != 0)
                    throw new InvalidOperationException(
                        $"PIC14 architecture does not support interrupt vector " +
                        $"0x{func.InterruptVector:X2}. Only 0x04 is supported.");

                if (isr0x04 != null)
                    throw new InvalidOperationException(
                        "Multiple interrupt handlers defined for vector 0x04.");
                isr0x04 = func;
            }
        }

        EmitComment("--- Interrupt Vector ---");
        if (isr0x04 != null)
        {
            EmitRaw($"\tORG 0x{intAddr:X4}");
            EmitLabel("__interrupt");

            EmitContextSave();

            _currentFunctionName = isr0x04.Name;
            _currentBank = -1;
            _currentBlockTerminated = false;

            foreach (var instr in isr0x04.Body)
            {
                if (_currentBlockTerminated)
                {
                    if (instr is Label)
                    {
                        _currentBlockTerminated = false;
                        CompileInstruction(instr);
                    }

                    continue;
                }

                if (instr is Return)
                {
                    EmitContextRestore();
                    EmitInterruptReturn();
                    _currentBlockTerminated = true;
                    continue;
                }

                CompileInstruction(instr);
            }

            if (!_currentBlockTerminated)
            {
                EmitContextRestore();
                EmitInterruptReturn();
            }
        }
        else
        {
            // Dummy interrupt handler
            EmitRaw($"\tORG 0x{intAddr:X4}");
            EmitLabel("__interrupt");
            Emit("RETFIE");
        }

        // Emit non-main, non-interrupt functions
        foreach (var func in program.Functions)
        {
            if (func.IsInterrupt) continue;
            if (func.IsInline && func.Name != "main") continue;
            if (func.Name == "main") continue;
            CompileFunction(func);
        }

        // Emit main at the end
        foreach (var func in program.Functions)
        {
            if (func.Name == "main")
            {
                CompileFunction(func);
                break;
            }
        }

        // Software arithmetic routines (emitted once, after all functions)
        EmitSoftwareRoutines();

        if (_needsErrTrap)
        {
            EmitComment("--- Uncaught error in main: park here (code in __err) ---");
            EmitLabel("__err_trap");
            Emit("GOTO", "__err_trap");
        }

        // Flash RETLW tables (const[str] / const[uint8[N]]), at the very end.
        EmitFlashTables();

        if (_usesFloat)
            EmitRaw("#include \"float.inc\"");

        EmitRaw("\tEND");

        // The bump allocator is bank-0 only: the compiled stack starts at 0x20
        // and dynamic cells grow upward. Fail loudly if the program outgrew the
        // window instead of silently aliasing SFRs or the scratch block.
        int ramLimit = NeedsMulDivScratch || _needsArrIdxScale
            ? _scratchBase
            : (Config.RamSize > 0 && Config.RamSize < 96 ? 0x50 : 0x80);
        if (_ramHead > ramLimit)
            throw new InvalidOperationException(
                $"PIC14: RAM allocation overflows the bank-0 window " +
                $"(head=0x{_ramHead:X2}, limit=0x{ramLimit:X2}). The PIC14 backend " +
                "allocates all variables in bank 0 starting at 0x20 " +
                "(plus the common-RAM scratch block when mul/div or arrays are used); " +
                "reduce locals/array sizes.");

        // Peephole optimization pass
        _assembly = PIC14Peephole.Optimize(_assembly);

        foreach (var line in _assembly)
            output.WriteLine(line.ToString());
    }

    // --- Function compilation ---

    private void CompileFunction(Function func)
    {
        EmitLabel(func.Name);
        _currentFunctionName = func.Name;
        _currentBank = -1;
        _currentBlockTerminated = false;

        foreach (var instr in func.Body)
        {
            if (_currentBlockTerminated)
            {
                if (instr is Label)
                {
                    _currentBlockTerminated = false;
                    CompileInstruction(instr);
                }

                continue;
            }

            CompileInstruction(instr);
        }
    }

    private void CompileInstruction(Instruction instr)
    {
        switch (instr)
        {
            case Return arg: CompileVariant(arg); break;
            case Copy arg: CompileVariant(arg); break;
            case LoadIndirect arg: CompileVariant(arg); break;
            case StoreIndirect arg: CompileVariant(arg); break;
            case Unary arg: CompileVariant(arg); break;
            case Binary arg: CompileVariant(arg); break;
            case BitSet arg: CompileVariant(arg); break;
            case BitClear arg: CompileVariant(arg); break;
            case BitCheck arg: CompileVariant(arg); break;
            case BitWrite arg: CompileVariant(arg); break;
            case AugAssign arg: CompileVariant(arg); break;
            case InlineAsm arg: CompileVariant(arg); break;
            case DebugLine arg: CompileVariant(arg); break;
            case Label arg: CompileVariant(arg); break;
            case Jump arg: CompileVariant(arg); break;
            case JumpIfZero arg: CompileVariant(arg); break;
            case JumpIfNotZero arg: CompileVariant(arg); break;
            case JumpIfEqual arg: CompileVariant(arg); break;
            case JumpIfNotEqual arg: CompileVariant(arg); break;
            case JumpIfLessThan arg: CompileVariant(arg); break;
            case JumpIfLessOrEqual arg: CompileVariant(arg); break;
            case JumpIfGreaterThan arg: CompileVariant(arg); break;
            case JumpIfGreaterOrEqual arg: CompileVariant(arg); break;
            case JumpIfBitSet arg: CompileVariant(arg); break;
            case JumpIfBitClear arg: CompileVariant(arg); break;
            case Call arg: CompileVariant(arg); break;
            case ArrayLoad arg: CompileVariant(arg); break;
            case ArrayStore arg: CompileVariant(arg); break;
            case FlashData arg: CompileVariant(arg); break;
            case ArrayLoadFlash arg: CompileVariant(arg); break;
            case FlashLoadPtr arg: CompileVariant(arg); break;
            case SignalError arg: CompileVariant(arg); break;
            case SignalSuccess arg: CompileVariant(arg); break;
            case BranchOnError arg: CompileVariant(arg); break;
            default:
                throw new NotSupportedException($"PIC14CodeGen: unsupported instruction {instr.GetType().Name}");
        }
    }

    // --- Software mul/div/mod ------------------------------------------------

    private static bool IsMulDivOp(IrBinOp op) =>
        op is IrBinOp.Mul or IrBinOp.Div or IrBinOp.FloorDiv or IrBinOp.Mod;

    // Routine selection runs at the WIDEST of the three operands: a uint16
    // dividend divided into a uint8 destination still needs the 16-bit routine.
    private int MulDivOpSize(Val src1, Val src2, Val dst) =>
        Math.Max(GetValSize(dst), Math.Max(GetValSize(src1), GetValSize(src2)));

    private void RecordMulDiv(IrBinOp op, int size)
    {
        if (op == IrBinOp.Mul)
        {
            if (size == 1) _needsMul8 = true;
            else _needsMul16 = true;
        }
        else
        {
            if (size == 1) _needsDiv8 = true;
            else _needsDiv16 = true;
        }
    }

    // Pre-scan (before the header is emitted) so the scratch EQUs land in the
    // preamble and the routines are appended after the last function.
    private void ScanForSoftwareRoutines(ProgramIR program)
    {
        foreach (var func in program.Functions)
        foreach (var instr in func.Body)
        {
            switch (instr)
            {
                case Binary b when IsMulDivOp(b.Op):
                {
                    int size = MulDivOpSize(b.Src1, b.Src2, b.Dst);
                    if (size <= 2) RecordMulDiv(b.Op, size);
                    break;
                }
                case AugAssign a when IsMulDivOp(a.Op):
                {
                    int size = MulDivOpSize(a.Target, a.Operand, a.Target);
                    if (size <= 2) RecordMulDiv(a.Op, size);
                    break;
                }
                case ArrayLoad al when al.Index is not Constant && al.ElemType.SizeOf() == 2:
                case ArrayStore ast when ast.Index is not Constant && ast.ElemType.SizeOf() == 2:
                    _needsArrIdxScale = true;
                    break;
            }
        }
    }

    private void EmitScratchSymbols()
    {
        if (!NeedsMulDivScratch && !_needsArrIdxScale) return;

        EmitComment("--- Common-RAM scratch (bank-independent) ---");
        if (NeedsMulDivScratch)
        {
            _symbolTable[ScratchA] = _scratchBase + 0;
            _symbolTable[ScratchB] = _scratchBase + 2;
            _symbolTable[ScratchR] = _scratchBase + 4;
            _symbolTable[ScratchC] = _scratchBase + 6;
            EmitRaw($"{ScratchA} EQU 0x{_scratchBase + 0:X2}");
            EmitRaw($"{ScratchB} EQU 0x{_scratchBase + 2:X2}");
            EmitRaw($"{ScratchR} EQU 0x{_scratchBase + 4:X2}");
            EmitRaw($"{ScratchC} EQU 0x{_scratchBase + 6:X2}");
        }

        if (_needsArrIdxScale)
        {
            _symbolTable[ScratchIdx] = _scratchBase + 7;
            EmitRaw($"{ScratchIdx} EQU 0x{_scratchBase + 7:X2}");
        }

        EmitRaw("");
    }

    // Loads a 1- or 2-byte value into a 16-bit scratch cell (zero-extending
    // 8-bit sources). Scratch cells are in common RAM: no bank select needed.
    private void LoadScratch16(string cell, Val src)
    {
        if (src is Constant c)
        {
            int v = c.Value;
            Emit("MOVLW", $"0x{v & 0xFF:X2}");
            Emit("MOVWF", cell);
            if (((v >> 8) & 0xFF) == 0)
            {
                Emit("CLRF", cell + "+1");
            }
            else
            {
                Emit("MOVLW", $"0x{(v >> 8) & 0xFF:X2}");
                Emit("MOVWF", cell + "+1");
            }

            return;
        }

        LoadIntoW(src);
        Emit("MOVWF", cell);
        if (GetValSize(src) >= 2)
        {
            string lo = ResolveAddress(src);
            string hi = HighByteOperand(lo);
            SelectBank(hi);
            Emit("MOVF", hi, "W");
            Emit("MOVWF", cell + "+1");
        }
        else
        {
            Emit("CLRF", cell + "+1");
        }
    }

    private void CompileMulDivMod(Binary arg)
    {
        int opSize = MulDivOpSize(arg.Src1, arg.Src2, arg.Dst);
        if (opSize > 2)
            throw new NotSupportedException(
                "PIC14: Mul/Div/Mod wider than 16 bits is not supported");

        bool isMul = arg.Op == IrBinOp.Mul;
        bool isMod = arg.Op == IrBinOp.Mod;
        RecordMulDiv(arg.Op, opSize); // defensive; the pre-scan already did this

        if (opSize == 1)
        {
            LoadIntoW(arg.Src1);
            Emit("MOVWF", ScratchA);
            LoadIntoW(arg.Src2);
            Emit("MOVWF", ScratchB);
            Emit("CALL", isMul ? "__mul8" : "__div8");
            // __mul8: product in __m_r. __div8: quotient in __m_a, remainder in __m_r.
            Emit("MOVF", isMul || isMod ? ScratchR : ScratchA, "W");
            StoreWInto(arg.Dst);
            return;
        }

        LoadScratch16(ScratchA, arg.Src1);
        LoadScratch16(ScratchB, arg.Src2);
        Emit("CALL", isMul ? "__mul16" : "__div16");
        string res = isMul || isMod ? ScratchR : ScratchA;

        Emit("MOVF", res, "W");
        StoreWInto(arg.Dst); // low byte
        if (GetValSize(arg.Dst) == 2)
        {
            string dstHi = HighByteOperand(ResolveAddress(arg.Dst));
            Emit("MOVF", res + "+1", "W");
            SelectBank(dstHi);
            Emit("MOVWF", dstHi);
        }
    }

    // Routines operate exclusively on common-RAM scratch cells and STATUS, so
    // they are callable from any bank and never emit bank selects themselves.
    private void EmitSoftwareRoutines()
    {
        if (_needsMul8)
        {
            EmitComment("--- __mul8: __m_r = __m_a * __m_b (8x8 -> 8, shift-add) ---");
            EmitLabel("__mul8");
            Emit("CLRF", ScratchR);
            EmitLabel("__mul8_loop");
            Emit("MOVF", ScratchB, "F");
            Emit("BTFSC", "STATUS", "2");
            Emit("RETURN");
            Emit("BTFSS", ScratchB, "0");
            Emit("GOTO", "__mul8_skip");
            Emit("MOVF", ScratchA, "W");
            Emit("ADDWF", ScratchR, "F");
            EmitLabel("__mul8_skip");
            Emit("BCF", "STATUS", "0");
            Emit("RLF", ScratchA, "F");
            Emit("BCF", "STATUS", "0");
            Emit("RRF", ScratchB, "F");
            Emit("GOTO", "__mul8_loop");
        }

        if (_needsDiv8)
        {
            EmitComment("--- __div8: __m_a = __m_a / __m_b, __m_r = remainder (restoring) ---");
            EmitLabel("__div8");
            Emit("CLRF", ScratchR);
            Emit("MOVLW", "0x08");
            Emit("MOVWF", ScratchC);
            EmitLabel("__div8_loop");
            Emit("BCF", "STATUS", "0");
            Emit("RLF", ScratchA, "F");
            Emit("RLF", ScratchR, "F");
            Emit("BTFSC", "STATUS", "0"); // 9th remainder bit -> subtract for sure
            Emit("GOTO", "__div8_sub");
            Emit("MOVF", ScratchB, "W");
            Emit("SUBWF", ScratchR, "W");
            Emit("BTFSS", "STATUS", "0"); // C=0 -> remainder < divisor
            Emit("GOTO", "__div8_next");
            EmitLabel("__div8_sub");
            Emit("MOVF", ScratchB, "W");
            Emit("SUBWF", ScratchR, "F");
            Emit("BSF", ScratchA, "0");   // quotient bit (shifted into __m_a's LSB)
            EmitLabel("__div8_next");
            Emit("DECFSZ", ScratchC, "F");
            Emit("GOTO", "__div8_loop");
            Emit("RETURN");
        }

        if (_needsMul16)
        {
            EmitComment("--- __mul16: __m_r = __m_a * __m_b (16x16 -> 16, shift-add) ---");
            EmitLabel("__mul16");
            Emit("CLRF", ScratchR);
            Emit("CLRF", ScratchR + "+1");
            EmitLabel("__mul16_loop");
            Emit("MOVF", ScratchB, "W");
            Emit("IORWF", ScratchB + "+1", "W");
            Emit("BTFSC", "STATUS", "2");
            Emit("RETURN");
            Emit("BTFSS", ScratchB, "0");
            Emit("GOTO", "__mul16_skip");
            Emit("MOVF", ScratchA, "W");
            Emit("ADDWF", ScratchR, "F");
            Emit("BTFSC", "STATUS", "0");
            Emit("INCF", ScratchR + "+1", "F");
            Emit("MOVF", ScratchA + "+1", "W");
            Emit("ADDWF", ScratchR + "+1", "F");
            EmitLabel("__mul16_skip");
            Emit("BCF", "STATUS", "0");
            Emit("RLF", ScratchA, "F");
            Emit("RLF", ScratchA + "+1", "F");
            Emit("BCF", "STATUS", "0");
            Emit("RRF", ScratchB + "+1", "F");
            Emit("RRF", ScratchB, "F");
            Emit("GOTO", "__mul16_loop");
        }

        if (_needsDiv16)
        {
            EmitComment("--- __div16: __m_a = __m_a / __m_b, __m_r = remainder (restoring) ---");
            EmitLabel("__div16");
            Emit("CLRF", ScratchR);
            Emit("CLRF", ScratchR + "+1");
            Emit("MOVLW", "0x10");
            Emit("MOVWF", ScratchC);
            EmitLabel("__div16_loop");
            Emit("BCF", "STATUS", "0");
            Emit("RLF", ScratchA, "F");
            Emit("RLF", ScratchA + "+1", "F");
            Emit("RLF", ScratchR, "F");
            Emit("RLF", ScratchR + "+1", "F");
            Emit("BTFSC", "STATUS", "0"); // 17th remainder bit -> subtract for sure
            Emit("GOTO", "__div16_sub");
            Emit("MOVF", ScratchB + "+1", "W");
            Emit("SUBWF", ScratchR + "+1", "W");
            Emit("BTFSS", "STATUS", "2"); // high bytes differ -> carry decides
            Emit("GOTO", "__div16_chk");
            Emit("MOVF", ScratchB, "W");  // high bytes equal -> compare low bytes
            Emit("SUBWF", ScratchR, "W");
            EmitLabel("__div16_chk");
            Emit("BTFSS", "STATUS", "0"); // C=0 -> remainder < divisor
            Emit("GOTO", "__div16_next");
            EmitLabel("__div16_sub");
            Emit("MOVF", ScratchB, "W");
            Emit("SUBWF", ScratchR, "F");
            Emit("BTFSS", "STATUS", "0");
            Emit("DECF", ScratchR + "+1", "F");
            Emit("MOVF", ScratchB + "+1", "W");
            Emit("SUBWF", ScratchR + "+1", "F");
            Emit("BSF", ScratchA, "0");   // quotient bit (shifted into __m_a's LSB)
            EmitLabel("__div16_next");
            Emit("DECFSZ", ScratchC, "F");
            Emit("GOTO", "__div16_loop");
            Emit("RETURN");
        }
    }

    // --- Error propagation (T-flag model, PIC14 flavor) ------------------------
    //
    // PIC14 has no spare CPU flag that survives across CALL/RETURN, so the
    // error channel is a RAM cell: __err == 0 means success, nonzero holds the
    // error code. CanFail functions clear it on the happy path (SignalSuccess)
    // and callers test it right after the CALL (BranchOnError). An uncaught
    // error in main cannot RETURN (main is entered via GOTO from the reset
    // vector), so it parks in the __err_trap idle loop instead.

    private bool _needsErrTrap;

    private void CompileVariant(SignalError arg)
    {
        string err = GetOrAllocVariable("__err");
        SelectBank(err);
        LoadIntoW(arg.Code);
        Emit("MOVWF", err);
        if (arg.CatchLabel != null)
        {
            Emit("GOTO", arg.CatchLabel);
        }
        else if (_currentFunctionName == "main")
        {
            _needsErrTrap = true;
            Emit("GOTO", "__err_trap");
        }
        else
        {
            Emit("RETURN"); // propagate to the caller's BranchOnError
        }

        _currentBlockTerminated = true;
    }

    private void CompileVariant(SignalSuccess arg)
    {
        string err = GetOrAllocVariable("__err");
        SelectBank(err);
        Emit("CLRF", err);
    }

    private void CompileVariant(BranchOnError arg)
    {
        string err = GetOrAllocVariable("__err");
        SelectBank(err);
        Emit("MOVF", err, "F");       // Z = (__err == 0)
        Emit("BTFSS", "STATUS", "2"); // Z set (no error) -> skip the branch
        Emit("GOTO", arg.ErrorLabel);
    }

    // --- Arrays (RAM, direct or FSR/INDF) -------------------------------------

    private string HighByteOperand(string lo) =>
        lo.StartsWith("0x") ? $"0x{ParseHexAddr(lo) + 1:X2}" : lo + "+1";

    private string OffsetOperand(string baseOp, int offset)
    {
        if (offset == 0) return baseOp;
        return baseOp.StartsWith("0x")
            ? $"0x{ParseHexAddr(baseOp) + offset:X2}"
            : baseOp + "+" + offset;
    }

    private string ArrayBaseOperand(string arrayName)
    {
        if (_stackLayout.ContainsKey(arrayName) || _symbolTable.ContainsKey(arrayName))
            return arrayName;
        // The StackAllocator registers every array it sees (locals and globals),
        // so a miss means the IR referenced an array with no storage.
        throw new InvalidOperationException(
            $"PIC14: array '{arrayName}' has no allocated storage");
    }

    private void CheckConstIndex(string arrayName, int index, int count)
    {
        if (index < 0 || index >= count)
            throw new InvalidOperationException(
                $"PIC14: constant index {index} is out of bounds for array " +
                $"'{arrayName}' of length {count}");
    }

    // Leaves FSR pointing at &array[index] (scaled by element size). All PIC14
    // arrays live in bank 0 (< 0x100), so IRP is force-cleared.
    private void EmitArrayElementFsr(string arrayName, Val index, int elemSize)
    {
        string baseOp = ArrayBaseOperand(arrayName);
        if (elemSize == 2)
        {
            LoadIntoW(index);
            Emit("MOVWF", ScratchIdx);
            Emit("BCF", "STATUS", "0");
            Emit("RLF", ScratchIdx, "F"); // index * 2
            Emit("MOVF", ScratchIdx, "W");
        }
        else
        {
            LoadIntoW(index);
        }

        Emit("ADDLW", baseOp);            // W = &array[0] + scaled index
        Emit("MOVWF", "FSR");
        Emit("BCF", "STATUS", "7");       // IRP = 0
    }

    private void CompileVariant(ArrayLoad arg)
    {
        int elemSize = arg.ElemType.SizeOf();
        if (elemSize > 2)
            throw new NotSupportedException(
                "PIC14: array elements wider than 16 bits are not supported");

        int dstSize = GetValSize(arg.Dst);

        if (arg.Index is Constant ci)
        {
            CheckConstIndex(arg.ArrayName, ci.Value, arg.Count);
            string baseOp = ArrayBaseOperand(arg.ArrayName);
            string lo = OffsetOperand(baseOp, ci.Value * elemSize);
            SelectBank(lo);
            Emit("MOVF", lo, "W");
            StoreWInto(arg.Dst);
            if (dstSize == 2)
            {
                string dstHi = HighByteOperand(ResolveAddress(arg.Dst));
                if (elemSize == 2)
                {
                    string hi = OffsetOperand(baseOp, ci.Value * elemSize + 1);
                    SelectBank(hi);
                    Emit("MOVF", hi, "W");
                    SelectBank(dstHi);
                    Emit("MOVWF", dstHi);
                }
                else
                {
                    SelectBank(dstHi);
                    Emit("CLRF", dstHi); // zero-extend byte element into 16-bit dst
                }
            }

            return;
        }

        EmitArrayElementFsr(arg.ArrayName, arg.Index, elemSize);
        Emit("MOVF", "INDF", "W");
        StoreWInto(arg.Dst);
        if (dstSize == 2)
        {
            string dstHi = HighByteOperand(ResolveAddress(arg.Dst));
            if (elemSize == 2)
            {
                Emit("INCF", "FSR", "F");
                Emit("MOVF", "INDF", "W");
                SelectBank(dstHi);
                Emit("MOVWF", dstHi);
            }
            else
            {
                SelectBank(dstHi);
                Emit("CLRF", dstHi);
            }
        }
    }

    private void CompileVariant(ArrayStore arg)
    {
        int elemSize = arg.ElemType.SizeOf();
        if (elemSize > 2)
            throw new NotSupportedException(
                "PIC14: array elements wider than 16 bits are not supported");
        if (arg.Src is FunctionRef)
            throw new NotSupportedException(
                "PIC14: Callable arrays are not supported");

        if (arg.Index is Constant ci)
        {
            CheckConstIndex(arg.ArrayName, ci.Value, arg.Count);
            string baseOp = ArrayBaseOperand(arg.ArrayName);
            string lo = OffsetOperand(baseOp, ci.Value * elemSize);
            LoadIntoW(arg.Src);
            SelectBank(lo);
            Emit("MOVWF", lo);
            if (elemSize == 2)
            {
                string hi = OffsetOperand(baseOp, ci.Value * elemSize + 1);
                if (arg.Src is Constant sc)
                {
                    int hv = (sc.Value >> 8) & 0xFF;
                    SelectBank(hi);
                    if (hv == 0)
                    {
                        Emit("CLRF", hi);
                    }
                    else
                    {
                        Emit("MOVLW", $"0x{hv:X2}");
                        Emit("MOVWF", hi);
                    }
                }
                else if (GetValSize(arg.Src) >= 2)
                {
                    string srcHi = HighByteOperand(ResolveAddress(arg.Src));
                    SelectBank(srcHi);
                    Emit("MOVF", srcHi, "W");
                    SelectBank(hi);
                    Emit("MOVWF", hi);
                }
                else
                {
                    SelectBank(hi);
                    Emit("CLRF", hi);
                }
            }

            return;
        }

        EmitArrayElementFsr(arg.ArrayName, arg.Index, elemSize);
        LoadIntoW(arg.Src);
        Emit("MOVWF", "INDF");
        if (elemSize == 2)
        {
            Emit("INCF", "FSR", "F");
            if (arg.Src is Constant sc2)
            {
                int hv = (sc2.Value >> 8) & 0xFF;
                if (hv == 0)
                {
                    Emit("CLRF", "INDF");
                }
                else
                {
                    Emit("MOVLW", $"0x{hv:X2}");
                    Emit("MOVWF", "INDF");
                }
            }
            else if (GetValSize(arg.Src) >= 2)
            {
                string srcHi = HighByteOperand(ResolveAddress(arg.Src));
                SelectBank(srcHi);
                Emit("MOVF", srcHi, "W");
                Emit("MOVWF", "INDF");
            }
            else
            {
                Emit("CLRF", "INDF");
            }
        }
    }

    // --- Flash tables (RETLW + computed GOTO) ----------------------------------
    //
    // Layout of every table (see the field comments near _flashData):
    //
    //   __flash_x:                      ; CALL target (index in W, PCLATH = HIGH(__flash_x_t))
    //       ADDLW LOW(__flash_x_t)      ; W = low byte of entry address
    //       BTFSC STATUS, 0
    //       INCF  PCLATH, F             ; carry of the ADDLW into the high byte
    //       MOVWF PCL                   ; PC = PCLATH<4:0>:W -> lands on the entry
    //   __flash_x_t:
    //       RETLW 0x..                  ; entry 0 (returns to the CALLer of __flash_x)
    //       ...
    //   __flash_x_e:
    //
    // MOVWF PCL writes all 13 PC bits (PCLATH<4:0> : W), so entries may cross
    // 256-word and even 2K-page boundaries. The only page-sensitive step is the
    // CALL itself (PC<12:11> come from PCLATH<4:3>, which we loaded with the
    // entry address): asserted at assembly time below.

    private void CompileVariant(FlashData arg)
    {
        // Data definition: registered here, emitted at the end by EmitFlashTables.
        if (!_flashDataNames.Add(arg.Name)) return; // interned duplicates
        if (arg.Bytes.Count > 256)
            throw new NotSupportedException(
                $"PIC14: flash table '{arg.Name}' has {arg.Bytes.Count} bytes; " +
                "runtime table indexes are 8-bit, so tables are capped at 256 bytes");
        _flashData.Add((arg.Name, arg.Bytes));
    }

    private void CompileVariant(ArrayLoadFlash arg)
    {
        // Dst = flash_table[Index]. PCLATH is a core register (mirrored in every
        // bank), so no bank selects are needed around it.
        string tbl = FlashLabel(arg.ArrayName);
        Emit("MOVLW", $"HIGH({tbl}_t)");
        Emit("MOVWF", "PCLATH");
        LoadIntoW(arg.Index); // 8-bit index (tables are capped at 256 entries)
        Emit("CALL", tbl);
        // The rest of the backend assumes page 0 (PCLATH untouched elsewhere):
        // restore it so later GOTO/CALL pages stay correct.
        Emit("CLRF", "PCLATH");
        StoreWInto(arg.Dst);
    }

    private void CompileVariant(FlashLoadPtr arg)
    {
        // Dst = flash[Ptr + Index], where Ptr is a RUNTIME 16-bit flash word
        // address (a const[str] passed by reference, e.g. uart_write_str's
        // parameter). The entry address is computed into PCLATH:W and reached
        // through the shared __flash_jmp trampoline (MOVWF PCL); the RETLW at
        // the entry returns to OUR caller frame (the CALL below).
        if (arg.Ptr is FlashStrAddr fsa)
        {
            // Degenerate case: the pointer is a compile-time label after all.
            CompileVariant(new ArrayLoadFlash(fsa.Name, arg.Index, arg.Dst));
            return;
        }

        _needsFlashJmp = true;
        string lo = ResolveAddress(arg.Ptr);
        string hi = HighByteOperand(lo);
        SelectBank(hi);
        Emit("MOVF", hi, "W");
        Emit("MOVWF", "PCLATH");
        LoadIntoW(arg.Index); // 8-bit index (flash strings are < 256 bytes)
        SelectBank(lo);
        Emit("ADDWF", lo, "W");       // W = lo(ptr) + index
        Emit("BTFSC", "STATUS", "0");
        Emit("INCF", "PCLATH", "F");  // carry into the high byte
        Emit("CALL", "__flash_jmp");
        Emit("CLRF", "PCLATH");
        StoreWInto(arg.Dst);
    }

    private void EmitFlashTables()
    {
        if (_flashData.Count == 0)
        {
            if (_needsFlashJmp)
                throw new InvalidOperationException(
                    "PIC14: FlashLoadPtr was emitted but no FlashData table exists");
            return;
        }

        EmitComment("--- Flash tables (RETLW lists, computed GOTO; see PIC14CodeGen) ---");
        if (_needsFlashJmp)
        {
            EmitComment("Shared trampoline: full entry address in PCLATH<4:0>:W.");
            EmitLabel("__flash_jmp");
            Emit("MOVWF", "PCL");
        }

        foreach (var (name, bytes) in _flashData)
        {
            string tbl = FlashLabel(name);
            EmitLabel(tbl);
            Emit("ADDLW", $"LOW({tbl}_t)");
            Emit("BTFSC", "STATUS", "0");
            Emit("INCF", "PCLATH", "F");
            Emit("MOVWF", "PCL");
            EmitLabel($"{tbl}_t");
            // Raw lines: the peephole never touches Raw, so the RETLW list and
            // the table layout survive every optimization pass untouched.
            foreach (int b in bytes)
                EmitRaw($"\tRETLW 0x{b & 0xFF:X2}");
            EmitLabel($"{tbl}_e");

            // Assembly-time guards for the page-sensitive steps. gpasm resolves
            // both labels by now (absolute mode, ORG 0).
            // 1. CALL <tbl> executes with PCLATH = HIGH(<tbl>_t): PC<12:11> come
            //    from PCLATH<4:3>, so the prologue must share the entries' page.
            EmitRaw($"\tIF (({tbl} >> 11) != (({tbl}_t) >> 11))");
            EmitRaw($"\tERROR \"{tbl}: prologue and entries straddle a 2K page boundary\"");
            EmitRaw("\tENDIF");
            if (_needsFlashJmp)
            {
                // 2. CALL __flash_jmp executes with PCLATH = HIGH(entry): every
                //    entry must live in the trampoline's 2K page.
                EmitRaw($"\tIF ((__flash_jmp >> 11) != (({tbl}_t) >> 11)) || ((__flash_jmp >> 11) != (({tbl}_e - 1) >> 11))");
                EmitRaw($"\tERROR \"{tbl}: entries and __flash_jmp straddle a 2K page boundary\"");
                EmitRaw("\tENDIF");
            }
        }

        EmitRaw("");
    }

    // --- Logic Helpers ---

    private void LoadIntoW(Val val)
    {
        if (val is NoneVal)
        {
            Emit("MOVLW", "0x00");
            return;
        }

        if (val is FlashStrAddr)
            throw new NotSupportedException(
                "PIC14: a flash string address is 16-bit and cannot be loaded " +
                "into W; it must flow through a 16-bit Copy (function argument)");

        if (val is Constant c)
        {
            Emit("MOVLW", $"0x{c.Value & 0xFF:X2}");
        }
        else
        {
            string op = ResolveAddress(val);
            SelectBank(op);
            Emit("MOVF", op, "W");
        }
    }

    private void StoreWInto(Val val)
    {
        if (val is NoneVal) return;
        string op = ResolveAddress(val);
        SelectBank(op);
        Emit("MOVWF", op);
    }

    // --- Instruction Visitors ---

    private void CompileVariant(Return arg)
    {
        if (arg.Value is not NoneVal)
            LoadIntoW(arg.Value);
        Emit("RETURN");
        _currentBlockTerminated = true;
    }

    private void CompileVariant(Copy arg)
    {
        int size = 1;
        string name = "";
        if (arg.Dst is Variable dv) name = dv.Name;
        else if (arg.Dst is Temporary dt) name = dt.Name;
        else if (arg.Dst is MemoryAddress dma)
            size = (dma.Type == DataType.UINT16) ? 2 : 1;

        if (!string.IsNullOrEmpty(name) && _varSizes.ContainsKey(name))
            size = _varSizes[name];

        if (size == 1)
        {
            LoadIntoW(arg.Src);
            StoreWInto(arg.Dst);
            return;
        }

        if (size == 2)
        {
            string dstLo = ResolveAddress(arg.Dst);
            string dstHi = dstLo.StartsWith("0x")
                ? $"0x{ParseHexAddr(dstLo) + 1:X2}"
                : dstLo + "+1";

            if (arg.Src is FlashStrAddr fsa)
            {
                // 16-bit flash word address of the string's RETLW entries
                // (consumed byte-by-byte via FlashLoadPtr in the callee).
                string entries = FlashLabel(fsa.Name) + "_t";
                Emit("MOVLW", $"LOW({entries})");
                SelectBank(dstLo);
                Emit("MOVWF", dstLo);
                Emit("MOVLW", $"HIGH({entries})");
                SelectBank(dstHi);
                Emit("MOVWF", dstHi);
            }
            else if (arg.Src is Constant sc)
            {
                int val = sc.Value;
                Emit("MOVLW", $"0x{val & 0xFF:X2}");
                SelectBank(dstLo);
                Emit("MOVWF", dstLo);
                Emit("MOVLW", $"0x{(val >> 8) & 0xFF:X2}");
                SelectBank(dstHi);
                Emit("MOVWF", dstHi);
            }
            else
            {
                string srcLo = ResolveAddress(arg.Src);

                SelectBank(srcLo);
                Emit("MOVF", srcLo, "W");
                SelectBank(dstLo);
                Emit("MOVWF", dstLo);

                if (GetValSize(arg.Src) >= 2)
                {
                    string srcHi = HighByteOperand(srcLo);
                    SelectBank(srcHi);
                    Emit("MOVF", srcHi, "W");
                    SelectBank(dstHi);
                    Emit("MOVWF", dstHi);
                }
                else
                {
                    // Widening copy from an 8-bit source: zero-extend instead of
                    // reading src+1 (which is the NEXT variable's slot).
                    SelectBank(dstHi);
                    Emit("CLRF", dstHi);
                }
            }

            return;
        }

        if (size == 4)
        {
            if (arg.Src is Constant sc4)
            {
                int val = sc4.Value;
                string dstBase = ResolveAddress(arg.Dst);
                for (int i = 0; i < 4; i++)
                {
                    int byteVal = (val >> (i * 8)) & 0xFF;
                    Emit("MOVLW", $"0x{byteVal:X2}");
                    string dstByte = i == 0
                        ? dstBase
                        : dstBase.StartsWith("0x")
                            ? $"0x{ParseHexAddr(dstBase) + i:X2}"
                            : dstBase + "+" + i;
                    SelectBank(dstByte);
                    Emit("MOVWF", dstByte);
                }

                return;
            }

            if (arg.Src is Variable)
            {
                string srcBase = ResolveAddress(arg.Src);
                string dstBase = ResolveAddress(arg.Dst);
                for (int i = 0; i < 4; i++)
                {
                    string srcByte = i == 0
                        ? srcBase
                        : srcBase.StartsWith("0x")
                            ? $"0x{ParseHexAddr(srcBase) + i:X2}"
                            : srcBase + "+" + i;
                    string dstByte = i == 0
                        ? dstBase
                        : dstBase.StartsWith("0x")
                            ? $"0x{ParseHexAddr(dstBase) + i:X2}"
                            : dstBase + "+" + i;
                    SelectBank(srcByte);
                    Emit("MOVF", srcByte, "W");
                    SelectBank(dstByte);
                    Emit("MOVWF", dstByte);
                }

                return;
            }
        }

        throw new NotSupportedException("PIC14: Copy only supports 1, 2, 4 bytes");
    }

    private void CompileVariant(LoadIndirect arg)
    {
        // PIC14 Indirect Addressing: FSR / INDF
        if (arg.SrcPtr is Constant cp)
        {
            Emit("MOVLW", $"0x{cp.Value & 0xFF:X2}");
            Emit("MOVWF", "FSR");
        }
        else
        {
            string ptrAddr = ResolveAddress(arg.SrcPtr);
            SelectBank(ptrAddr);
            Emit("MOVF", ptrAddr, "W");
            Emit("MOVWF", "FSR");
        }

        Emit("MOVF", "INDF", "W");
        StoreWInto(arg.Dst);
    }

    private void CompileVariant(StoreIndirect arg)
    {
        if (arg.DstPtr is Constant cp)
        {
            Emit("MOVLW", $"0x{cp.Value & 0xFF:X2}");
            Emit("MOVWF", "FSR");
        }
        else
        {
            string ptrAddr = ResolveAddress(arg.DstPtr);
            SelectBank(ptrAddr);
            Emit("MOVF", ptrAddr, "W");
            Emit("MOVWF", "FSR");
        }

        LoadIntoW(arg.Src);
        Emit("MOVWF", "INDF");
    }

    private void CompileVariant(Unary arg)
    {
        LoadIntoW(arg.Src);
        switch (arg.Op)
        {
            case IrUnOp.BitNot:
                Emit("XORLW", "0xFF");
                break;
            case IrUnOp.Not:
                Emit("XORLW", "1");
                Emit("ANDLW", "1");
                break;
            case IrUnOp.Neg:
                Emit("SUBLW", "0");
                break;
            default:
                throw new NotSupportedException($"PIC14: Unary op {arg.Op} not supported");
        }

        StoreWInto(arg.Dst);
    }

    // Operand size in bytes, resolved through the allocator's size table.
    private int GetValSize(Val val)
    {
        if (val is Variable v && _varSizes.ContainsKey(v.Name)) return _varSizes[v.Name];
        if (val is Temporary t && _varSizes.ContainsKey(t.Name)) return _varSizes[t.Name];
        if (val is MemoryAddress m && m.Type == DataType.UINT16) return 2;
        if (val is Constant c && (c.Value > 255 || c.Value < 0)) return 2;
        if (val is FlashStrAddr) return 2; // 16-bit flash word address
        return 1;
    }

    private void CompileVariant(Binary arg)
    {
        bool isComparison = arg.Op switch
        {
            IrBinOp.Equal or IrBinOp.NotEqual or IrBinOp.LessThan or
                IrBinOp.LessEqual or IrBinOp.GreaterThan or IrBinOp.GreaterEqual => true,
            _ => false
        };

        int size = isComparison
            ? Math.Max(GetValSize(arg.Src1), GetValSize(arg.Src2))
            : GetValSize(arg.Dst);

        // Check for Float
        bool IsFloat(Val val) => val switch
        {
            Variable v => v.Type == DataType.FLOAT,
            Temporary t => t.Type == DataType.FLOAT,
            _ => false
        };

        if (IsFloat(arg.Dst) || IsFloat(arg.Src1) || IsFloat(arg.Src2))
        {
            if (arg.Op == IrBinOp.Add)
            {
                string dstAddr = ResolveAddress(arg.Dst);
                CompileVariant(new Copy(arg.Src1, arg.Dst));
                string src2Addr = ResolveAddress(arg.Src2);
                EmitFloatAdd(dstAddr, src2Addr);
                return;
            }

            throw new NotSupportedException("PIC14: Float op not supported (only Add)");
        }

        // --- Multiply / Divide / Modulo (software routines) ---
        if (IsMulDivOp(arg.Op))
        {
            CompileMulDivMod(arg);
            return;
        }

        // --- 16-bit Implementation ---
        if (size == 2)
        {
            if (!isComparison)
            {
                CompileVariant(new Copy(arg.Src1, arg.Dst));
                switch (arg.Op)
                {
                    case IrBinOp.Add:
                    case IrBinOp.Sub:
                    case IrBinOp.BitAnd:
                    case IrBinOp.BitOr:
                    case IrBinOp.BitXor:
                    case IrBinOp.LShift:
                    case IrBinOp.RShift:
                        CompileVariant(new AugAssign(arg.Op, arg.Dst, arg.Src2));
                        break;
                    default:
                        throw new NotSupportedException("PIC14: 16-bit Binary Op not supported");
                }

                return;
            }
            else
            {
                // 16-bit Comparison -> 8-bit boolean result (0 or 1)
                string dstAddr = ResolveAddress(arg.Dst);
                SelectBank(dstAddr);
                Emit("CLRF", dstAddr);

                void LoadByte(Val v, bool high)
                {
                    if (v is Constant c)
                    {
                        int bval = high ? ((c.Value >> 8) & 0xFF) : (c.Value & 0xFF);
                        Emit("MOVLW", $"0x{bval:X2}");
                    }
                    else
                    {
                        string addr = ResolveAddress(v);
                        if (high)
                            addr = addr.StartsWith("0x")
                                ? $"0x{ParseHexAddr(addr) + 1:X2}"
                                : addr + "+1";
                        SelectBank(addr);
                        Emit("MOVF", addr, "W");
                    }
                }

                string GetAddr(Val v, bool high)
                {
                    string addr = ResolveAddress(v);
                    if (high)
                        addr = addr.StartsWith("0x")
                            ? $"0x{ParseHexAddr(addr) + 1:X2}"
                            : addr + "+1";
                    return addr;
                }

                string labelTrue = MakeLabel("cmp_true");
                string labelEnd = MakeLabel("cmp_end");

                if (arg.Op == IrBinOp.Equal)
                {
                    LoadByte(arg.Src2, true);
                    string src1Hi = GetAddr(arg.Src1, true);
                    if (arg.Src1 is Constant sc1)
                        Emit("XORLW", $"0x{(sc1.Value >> 8) & 0xFF:X2}");
                    else
                    {
                        SelectBank(src1Hi);
                        Emit("XORWF", src1Hi, "W");
                    }

                    Emit("BTFSS", "STATUS", "2");
                    Emit("GOTO", labelEnd);

                    LoadByte(arg.Src2, false);
                    string src1Lo = GetAddr(arg.Src1, false);
                    if (arg.Src1 is Constant sc1b)
                        Emit("XORLW", $"0x{sc1b.Value & 0xFF:X2}");
                    else
                    {
                        SelectBank(src1Lo);
                        Emit("XORWF", src1Lo, "W");
                    }

                    Emit("BTFSS", "STATUS", "2");
                    Emit("GOTO", labelEnd);

                    SelectBank(dstAddr);
                    Emit("INCF", dstAddr, "F");
                    EmitLabel(labelEnd);
                    return;
                }

                if (arg.Op == IrBinOp.NotEqual)
                {
                    LoadByte(arg.Src2, true);
                    string src1Hi = GetAddr(arg.Src1, true);
                    if (arg.Src1 is Constant sc1)
                        Emit("XORLW", $"0x{(sc1.Value >> 8) & 0xFF:X2}");
                    else
                    {
                        SelectBank(src1Hi);
                        Emit("XORWF", src1Hi, "W");
                    }

                    Emit("BTFSS", "STATUS", "2");
                    Emit("GOTO", labelTrue);

                    LoadByte(arg.Src2, false);
                    string src1Lo = GetAddr(arg.Src1, false);
                    if (arg.Src1 is Constant sc1b)
                        Emit("XORLW", $"0x{sc1b.Value & 0xFF:X2}");
                    else
                    {
                        SelectBank(src1Lo);
                        Emit("XORWF", src1Lo, "W");
                    }

                    Emit("BTFSC", "STATUS", "2");
                    Emit("GOTO", labelEnd);

                    EmitLabel(labelTrue);
                    SelectBank(dstAddr);
                    Emit("INCF", dstAddr, "F");
                    EmitLabel(labelEnd);
                    return;
                }

                // <, <=, >, >= (Unsigned assumed)
                LoadByte(arg.Src2, true);
                string s1Hi = GetAddr(arg.Src1, true);
                if (arg.Src1 is Constant cHi)
                    Emit("SUBLW", $"0x{(cHi.Value >> 8) & 0xFF:X2}");
                else
                {
                    SelectBank(s1Hi);
                    Emit("SUBWF", s1Hi, "W");
                }

                string lblChk = MakeLabel("cmp_chk");
                Emit("BTFSS", "STATUS", "2");
                Emit("GOTO", lblChk);

                LoadByte(arg.Src2, false);
                string s1Lo = GetAddr(arg.Src1, false);
                if (arg.Src1 is Constant cLo)
                    Emit("SUBLW", $"0x{cLo.Value & 0xFF:X2}");
                else
                {
                    SelectBank(s1Lo);
                    Emit("SUBWF", s1Lo, "W");
                }

                EmitLabel(lblChk);

                if (arg.Op == IrBinOp.LessThan)
                {
                    Emit("BTFSS", "STATUS", "0");
                    Emit("GOTO", labelTrue);
                }
                else if (arg.Op == IrBinOp.GreaterEqual)
                {
                    Emit("BTFSC", "STATUS", "0");
                    Emit("GOTO", labelTrue);
                }
                else if (arg.Op == IrBinOp.GreaterThan)
                {
                    Emit("BTFSS", "STATUS", "0");
                    Emit("GOTO", labelEnd);
                    Emit("BTFSS", "STATUS", "2");
                    Emit("GOTO", labelTrue);
                }
                else if (arg.Op == IrBinOp.LessEqual)
                {
                    Emit("BTFSS", "STATUS", "0");
                    Emit("GOTO", labelTrue);
                    Emit("BTFSC", "STATUS", "2");
                    Emit("GOTO", labelTrue);
                }

                Emit("GOTO", labelEnd);
                EmitLabel(labelTrue);
                SelectBank(dstAddr);
                Emit("INCF", dstAddr, "F");
                EmitLabel(labelEnd);
                return;
            }
        }

        // --- 8-Bit Comparisons with Optimization ---
        if (isComparison && size == 1)
        {
            string dstAddr = ResolveAddress(arg.Dst);
            SelectBank(dstAddr);
            Emit("CLRF", dstAddr);

            string labelTrue = MakeLabel("cmp_true");
            string labelEnd = MakeLabel("cmp_end");

            // Optimization: RHS is a literal constant
            if (arg.Src2 is Constant c2Lit &&
                (arg.Src1 is Variable || arg.Src1 is Temporary || arg.Src1 is MemoryAddress))
            {
                int k = c2Lit.Value & 0xFF;
                string srcAddr = ResolveAddress(arg.Src1);
                SelectBank(srcAddr);
                Emit("MOVF", srcAddr, "W");

                if (arg.Op == IrBinOp.Equal)
                {
                    Emit("XORLW", $"0x{k:X2}");
                    Emit("BTFSC", "STATUS", "2");
                    Emit("GOTO", labelTrue);
                    Emit("GOTO", labelEnd);
                }
                else if (arg.Op == IrBinOp.NotEqual)
                {
                    Emit("XORLW", $"0x{k:X2}");
                    Emit("BTFSS", "STATUS", "2");
                    Emit("GOTO", labelTrue);
                    Emit("GOTO", labelEnd);
                }
                else if (arg.Op == IrBinOp.GreaterThan)
                {
                    Emit("SUBLW", $"0x{k:X2}");
                    Emit("BTFSS", "STATUS", "0");
                    Emit("GOTO", labelTrue);
                    Emit("GOTO", labelEnd);
                }
                else if (arg.Op == IrBinOp.LessEqual)
                {
                    Emit("SUBLW", $"0x{k:X2}");
                    Emit("BTFSC", "STATUS", "0");
                    Emit("GOTO", labelTrue);
                    Emit("GOTO", labelEnd);
                }
                else if (arg.Op == IrBinOp.LessThan)
                {
                    if (k == 0)
                        Emit("GOTO", labelEnd);
                    else
                    {
                        Emit("SUBLW", $"0x{k - 1:X2}");
                        Emit("BTFSC", "STATUS", "0");
                        Emit("GOTO", labelTrue);
                        Emit("GOTO", labelEnd);
                    }
                }
                else if (arg.Op == IrBinOp.GreaterEqual)
                {
                    if (k == 0)
                        Emit("GOTO", labelTrue);
                    else
                    {
                        Emit("SUBLW", $"0x{k - 1:X2}");
                        Emit("BTFSS", "STATUS", "0");
                        Emit("GOTO", labelTrue);
                        Emit("GOTO", labelEnd);
                    }
                }

                EmitLabel(labelTrue);
                SelectBank(dstAddr);
                Emit("INCF", dstAddr, "F");
                EmitLabel(labelEnd);
                return;
            }

            // Generic 8-bit comparison
            LoadIntoW(arg.Src2);
            string addr1 = ResolveAddress(arg.Src1);
            SelectBank(addr1);
            Emit("SUBWF", addr1, "W");

            switch (arg.Op)
            {
                case IrBinOp.Equal:
                    Emit("BTFSC", "STATUS", "2");
                    break;
                case IrBinOp.NotEqual:
                    Emit("BTFSS", "STATUS", "2");
                    break;
                case IrBinOp.LessThan:
                    Emit("BTFSS", "STATUS", "0");
                    break;
                case IrBinOp.GreaterEqual:
                    Emit("BTFSC", "STATUS", "0");
                    break;
                case IrBinOp.GreaterThan:
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
                case IrBinOp.LessEqual:
                {
                    string lblSet = MakeLabel("L_LE");
                    string lblSkip = MakeLabel("L_LES");
                    Emit("BTFSS", "STATUS", "0");
                    Emit("GOTO", lblSet);
                    Emit("BTFSC", "STATUS", "2");
                    Emit("GOTO", lblSkip);
                    EmitLabel(lblSet);
                    Emit("INCF", dstAddr, "F");
                    EmitLabel(lblSkip);
                    return;
                }
                default: break;
            }

            Emit("INCF", dstAddr, "F");
            EmitLabel(labelEnd);
            return;
        }

        if (!isComparison)
        {
            // --- Shift operations ---
            if (arg.Op == IrBinOp.LShift || arg.Op == IrBinOp.RShift)
            {
                string rotateOp = (arg.Op == IrBinOp.LShift) ? "RLF" : "RRF";

                if (arg.Src2 is Constant shiftC)
                {
                    int rawShift = shiftC.Value;
                    int byteOffset = rawShift / 8;
                    int bitShift = rawShift % 8;

                    if (arg.Op == IrBinOp.RShift && byteOffset > 0)
                    {
                        string srcAddr = ResolveAddress(arg.Src1);
                        string srcHi = srcAddr.StartsWith("0x")
                            ? $"0x{ParseHexAddr(srcAddr) + byteOffset:X2}"
                            : srcAddr + "+" + byteOffset;
                        SelectBank(srcHi);
                        Emit("MOVF", srcHi, "W");
                    }
                    else
                    {
                        LoadIntoW(arg.Src1);
                    }

                    StoreWInto(arg.Dst);

                    string dstAddr = ResolveAddress(arg.Dst);
                    for (int i = 0; i < bitShift; i++)
                    {
                        Emit("BCF", "STATUS", "0");
                        SelectBank(dstAddr);
                        Emit(rotateOp, dstAddr, "F");
                    }
                }
                else
                {
                    // Variable shift
                    LoadIntoW(arg.Src1);
                    StoreWInto(arg.Dst);

                    string dstAddr = ResolveAddress(arg.Dst);
                    string countAddr = ResolveAddress(arg.Src2);
                    string loopLabel = MakeLabel("shift");
                    string doneLabel = MakeLabel("shift_done");

                    SelectBank(countAddr);
                    Emit("MOVF", countAddr, "W");
                    Emit("BTFSC", "STATUS", "2");
                    Emit("GOTO", doneLabel);
                    EmitLabel(loopLabel);
                    Emit("BCF", "STATUS", "0");
                    SelectBank(dstAddr);
                    Emit(rotateOp, dstAddr, "F");
                    SelectBank(countAddr);
                    Emit("DECFSZ", countAddr, "F");
                    Emit("GOTO", loopLabel);
                    EmitLabel(doneLabel);
                }

                return;
            }

            // Optimization: src2 is a constant
            if (arg.Src2 is Constant c2)
            {
                LoadIntoW(arg.Src1);
                int val = c2.Value & 0xFF;
                string valStr = $"0x{val:X2}";
                switch (arg.Op)
                {
                    case IrBinOp.Add: Emit("ADDLW", valStr); break;
                    case IrBinOp.BitAnd: Emit("ANDLW", valStr); break;
                    case IrBinOp.BitOr: Emit("IORLW", valStr); break;
                    case IrBinOp.BitXor: Emit("XORLW", valStr); break;
                    case IrBinOp.Sub:
                        int negVal = (-c2.Value) & 0xFF;
                        Emit("ADDLW", $"0x{negVal:X2}");
                        break;
                    default: break;
                }

                StoreWInto(arg.Dst);
                return;
            }

            LoadIntoW(arg.Src2);
            string a1 = ResolveAddress(arg.Src1);

            switch (arg.Op)
            {
                case IrBinOp.Add:
                    if (arg.Src1 is Constant) Emit("ADDLW", a1);
                    else
                    {
                        SelectBank(a1);
                        Emit("ADDWF", a1, "W");
                    }

                    break;
                case IrBinOp.Sub:
                    if (arg.Src1 is Constant c1s) Emit("SUBLW", $"0x{c1s.Value & 0xFF:X2}");
                    else
                    {
                        SelectBank(a1);
                        Emit("SUBWF", a1, "W");
                    }

                    break;
                case IrBinOp.BitAnd:
                    if (arg.Src1 is Constant) Emit("ANDLW", a1);
                    else
                    {
                        SelectBank(a1);
                        Emit("ANDWF", a1, "W");
                    }

                    break;
                case IrBinOp.BitOr:
                    if (arg.Src1 is Constant) Emit("IORLW", a1);
                    else
                    {
                        SelectBank(a1);
                        Emit("IORWF", a1, "W");
                    }

                    break;
                case IrBinOp.BitXor:
                    if (arg.Src1 is Constant) Emit("XORLW", a1);
                    else
                    {
                        SelectBank(a1);
                        Emit("XORWF", a1, "W");
                    }

                    break;
                default: break;
            }

            StoreWInto(arg.Dst);
            return;
        }

        // Generic fallback comparison
        LoadIntoW(arg.Src2);
        string addr1g = ResolveAddress(arg.Src1);
        SelectBank(addr1g);
        Emit("SUBWF", addr1g, "W");

        string dstAddrG = ResolveAddress(arg.Dst);
        SelectBank(dstAddrG);
        Emit("CLRF", dstAddrG);

        switch (arg.Op)
        {
            case IrBinOp.Equal: Emit("BTFSC", "STATUS", "2"); break;
            case IrBinOp.NotEqual: Emit("BTFSS", "STATUS", "2"); break;
            case IrBinOp.LessThan: Emit("BTFSS", "STATUS", "0"); break;
            case IrBinOp.GreaterEqual: Emit("BTFSC", "STATUS", "0"); break;
            case IrBinOp.GreaterThan:
            {
                string lblSkip = MakeLabel("L_GT");
                Emit("BTFSS", "STATUS", "0");
                Emit("GOTO", lblSkip);
                Emit("BTFSC", "STATUS", "2");
                Emit("GOTO", lblSkip);
                Emit("INCF", dstAddrG, "F");
                EmitLabel(lblSkip);
                return;
            }
            case IrBinOp.LessEqual:
            {
                string lblSet = MakeLabel("L_LE");
                string lblSkip = MakeLabel("L_LES");
                Emit("BTFSS", "STATUS", "0");
                Emit("GOTO", lblSet);
                Emit("BTFSS", "STATUS", "2");
                Emit("GOTO", lblSkip);
                EmitLabel(lblSet);
                Emit("INCF", dstAddrG, "F");
                EmitLabel(lblSkip);
                return;
            }
            default: break;
        }

        Emit("INCF", dstAddrG, "F");
    }

    private void CompileVariant(BitSet arg)
    {
        string addr = ResolveAddress(arg.Target);
        SelectBank(addr);
        Emit("BSF", addr, arg.Bit.ToString());
    }

    private void CompileVariant(BitClear arg)
    {
        string addr = ResolveAddress(arg.Target);
        SelectBank(addr);
        Emit("BCF", addr, arg.Bit.ToString());
    }

    private void CompileVariant(BitCheck arg)
    {
        string addr = ResolveAddress(arg.Source);
        SelectBank(addr);

        string dstAddr = ResolveAddress(arg.Dst);
        SelectBank(dstAddr);
        Emit("CLRF", dstAddr);
        Emit("BTFSC", addr, arg.Bit.ToString());
        SelectBank(dstAddr);
        Emit("INCF", dstAddr, "F");
    }

    private void CompileVariant(BitWrite arg)
    {
        if (arg.Src is Constant c)
        {
            string addr = ResolveAddress(arg.Target);
            SelectBank(addr);
            if (c.Value != 0)
                Emit("BSF", addr, arg.Bit.ToString());
            else
                Emit("BCF", addr, arg.Bit.ToString());
            return;
        }

        LoadIntoW(arg.Src);
        Emit("IORLW", "0");

        string addrBW = ResolveAddress(arg.Target);
        string bitStr = arg.Bit.ToString();
        string lblZero = MakeLabel("L_BZ");
        string lblEnd = MakeLabel("L_BE");

        Emit("BTFSC", "STATUS", "2");
        Emit("GOTO", lblZero);
        SelectBank(addrBW);
        Emit("BSF", addrBW, bitStr);
        Emit("GOTO", lblEnd);
        EmitLabel(lblZero);
        SelectBank(addrBW);
        Emit("BCF", addrBW, bitStr);
        EmitLabel(lblEnd);
    }

    private void CompileVariant(Label arg)
    {
        EmitLabel(arg.Name);
        // Label could be a jump target from anywhere — bank state is unknown.
        _currentBank = -1;
    }

    private void CompileVariant(Jump arg) => Emit("GOTO", arg.Target);

    private void CompileVariant(JumpIfZero arg)
    {
        if (arg.Condition is Constant c)
        {
            if (c.Value == 0) Emit("GOTO", arg.Target);
            return;
        }

        LoadIntoW(arg.Condition);
        Emit("IORLW", "0");
        Emit("BTFSC", "STATUS", "2");
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(JumpIfNotZero arg)
    {
        if (arg.Condition is Constant c)
        {
            if (c.Value != 0) Emit("GOTO", arg.Target);
            return;
        }

        LoadIntoW(arg.Condition);
        Emit("IORLW", "0");
        Emit("BTFSS", "STATUS", "2");
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(Call arg)
    {
        // Copy arguments into the callee's parameter stack slots ("callee.param").
        // The compiled stack is static (no recursion), so parameter passing is a
        // plain Copy; sizes resolve through the allocator's tables (a const[str]
        // argument is a 16-bit FlashStrAddr into a 16-bit flash-pointer slot).
        if (arg.Args is { Count: > 0 })
        {
            if (!_funcByName.TryGetValue(arg.FunctionName, out var callee))
                throw new InvalidOperationException(
                    $"PIC14: call to unknown function '{arg.FunctionName}' with arguments");
            if (arg.Args.Count > callee.Params.Count)
                throw new InvalidOperationException(
                    $"PIC14: call to '{arg.FunctionName}' passes {arg.Args.Count} " +
                    $"arguments but it declares {callee.Params.Count} parameters");
            for (int i = 0; i < arg.Args.Count; i++)
                CompileVariant(new Copy(arg.Args[i], new Variable(callee.Params[i])));
        }

        Emit("CALL", arg.FunctionName);
        // Called function may change BSR; invalidate both tracking
        _currentBank = -1;
        _strategy.InvalidateBank();
        if (arg.Dst is Variable || arg.Dst is Temporary)
            StoreWInto(arg.Dst);
    }

    private void CompileVariant(JumpIfBitSet arg)
    {
        string addr = ResolveAddress(arg.Source);
        SelectBank(addr);
        // BTFSC: skip next if bit is CLEAR (execute GOTO only when bit is SET)
        Emit("BTFSC", addr, arg.Bit.ToString());
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(JumpIfBitClear arg)
    {
        string addr = ResolveAddress(arg.Source);
        SelectBank(addr);
        // BTFSS: skip next if bit is SET (execute GOTO only when bit is CLEAR)
        Emit("BTFSS", addr, arg.Bit.ToString());
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(AugAssign arg)
    {
        // target op= operand for mul/div/mod: lower onto the Binary path — the
        // scratch-cell calling convention is alias-safe (dst == src is fine).
        if (IsMulDivOp(arg.Op))
        {
            CompileMulDivMod(new Binary(arg.Op, arg.Target, arg.Operand, arg.Target));
            return;
        }

        string targetAddr = ResolveAddress(arg.Target);
        string targetAddrHi = targetAddr;

        int size = 1;
        string varName = "";
        if (arg.Target is Variable tv)
        {
            varName = tv.Name;
        }
        else if (arg.Target is Temporary tt)
        {
            varName = tt.Name;
        }

        if (!string.IsNullOrEmpty(varName) && _varSizes.ContainsKey(varName))
            size = _varSizes[varName];

        if (size > 1)
        {
            targetAddrHi = targetAddr.StartsWith("0x")
                ? $"0x{ParseHexAddr(targetAddr) + 1:X2}"
                : targetAddr + "+1";
        }

        // --- 8-bit ---
        if (size == 1)
        {
            // Load the operand FIRST (it may bank-select, e.g. a bank-1 SFR),
            // then select the target's bank for the read-modify-write.
            LoadIntoW(arg.Operand);

            switch (arg.Op)
            {
                case IrBinOp.Add: SelectBank(targetAddr); Emit("ADDWF", targetAddr, "F"); break;
                case IrBinOp.Sub: SelectBank(targetAddr); Emit("SUBWF", targetAddr, "F"); break;
                case IrBinOp.BitAnd: SelectBank(targetAddr); Emit("ANDWF", targetAddr, "F"); break;
                case IrBinOp.BitOr: SelectBank(targetAddr); Emit("IORWF", targetAddr, "F"); break;
                case IrBinOp.BitXor: SelectBank(targetAddr); Emit("XORWF", targetAddr, "F"); break;
                case IrBinOp.LShift:
                case IrBinOp.RShift:
                {
                    // Shift loop: __tmp (bank-0 counter) and the target may live
                    // in different banks, so every access is bank-selected. The
                    // bank state at the backedge equals the state at the loop
                    // head (bank of __tmp), keeping the linear tracker consistent.
                    string rotateOp = arg.Op == IrBinOp.LShift ? "RLF" : "RRF";
                    string loopLbl = MakeLabel(arg.Op == IrBinOp.LShift ? "augls" : "augrs");
                    string doneLbl = loopLbl + "_done";
                    GetOrAllocVariable("__tmp");
                    SelectBank("__tmp");
                    Emit("MOVWF", "__tmp");
                    Emit("MOVF", "__tmp", "F");
                    Emit("BTFSC", "STATUS", "2");
                    Emit("GOTO", doneLbl);
                    EmitLabel(loopLbl);
                    Emit("BCF", "STATUS", "0");
                    SelectBank(targetAddr);
                    Emit(rotateOp, targetAddr, "F");
                    SelectBank("__tmp");
                    Emit("DECFSZ", "__tmp", "F");
                    Emit("GOTO", loopLbl);
                    EmitLabel(doneLbl);
                    break;
                }
                default:
                    throw new NotSupportedException("PIC14: AugAssign 8-bit op not implemented");
            }

            return;
        }

        // --- 16-bit ---
        if (size == 2)
        {
            // 1. Bitwise Ops
            if (arg.Op == IrBinOp.BitAnd || arg.Op == IrBinOp.BitOr || arg.Op == IrBinOp.BitXor)
            {
                int valLo = 0, valHi = 0;
                bool isConst = false;
                if (arg.Operand is Constant c)
                {
                    valLo = c.Value & 0xFF;
                    valHi = (c.Value >> 8) & 0xFF;
                    isConst = true;
                }

                string opStr = arg.Op == IrBinOp.BitAnd ? "ANDWF"
                    : arg.Op == IrBinOp.BitOr ? "IORWF"
                    : "XORWF";
                string litOpStr = arg.Op == IrBinOp.BitAnd ? "ANDLW"
                    : arg.Op == IrBinOp.BitOr ? "IORLW"
                    : "XORLW";

                SelectBank(targetAddr);
                if (isConst)
                {
                    Emit("MOVF", targetAddr, "W");
                    Emit(litOpStr, $"0x{valLo:X2}");
                    Emit("MOVWF", targetAddr);
                }
                else
                {
                    LoadIntoW(arg.Operand);
                    Emit(opStr, targetAddr, "F");
                }

                // High byte
                SelectBank(targetAddrHi);
                if (isConst)
                {
                    Emit("MOVF", targetAddrHi, "W");
                    Emit(litOpStr, $"0x{valHi:X2}");
                    Emit("MOVWF", targetAddrHi);
                }
                else
                {
                    throw new NotSupportedException(
                        "PIC14: 16-bit AugAssign non-const bitwise not fully implemented");
                }

                return;
            }

            // 2. Add
            if (arg.Op == IrBinOp.Add)
            {
                if (arg.Operand is Constant addC)
                {
                    Emit("MOVLW", $"0x{addC.Value & 0xFF:X2}");
                    SelectBank(targetAddr);
                    Emit("ADDWF", targetAddr, "F");
                    Emit("BTFSC", "STATUS", "0");
                    Emit("INCF", targetAddrHi, "F");
                    Emit("MOVLW", $"0x{(addC.Value >> 8) & 0xFF:X2}");
                    SelectBank(targetAddrHi);
                    Emit("ADDWF", targetAddrHi, "F");
                }
                else
                {
                    // var16 += var16:
                    //   W = operand_lo; target_lo += W (sets Carry)
                    //   if carry: target_hi++
                    //   W = operand_hi; target_hi += W
                    string opAddr = ResolveAddress(arg.Operand);
                    SelectBank(opAddr);
                    Emit("MOVF", opAddr, "W");
                    SelectBank(targetAddr);
                    Emit("ADDWF", targetAddr, "F");
                    SelectBank(targetAddrHi);
                    Emit("BTFSC", "STATUS", "0");
                    Emit("INCF", targetAddrHi, "F");
                    if (GetValSize(arg.Operand) >= 2)
                    {
                        string opAddrHi = HighByteOperand(opAddr);
                        SelectBank(opAddrHi);
                        Emit("MOVF", opAddrHi, "W");
                        SelectBank(targetAddrHi);
                        Emit("ADDWF", targetAddrHi, "F");
                    }
                    // 8-bit operand: high byte is zero — the carry INCF above
                    // already completed the add. Reading operand+1 here would
                    // read the NEXT variable's RAM slot (same bug fixed in Copy).
                }

                return;
            }

            // 3. Sub
            if (arg.Op == IrBinOp.Sub)
            {
                int valLo = 0, valHi = 0;
                bool isConst = false;
                if (arg.Operand is Constant c)
                {
                    valLo = c.Value & 0xFF;
                    valHi = (c.Value >> 8) & 0xFF;
                    isConst = true;
                }

                if (isConst)
                    Emit("MOVLW", $"0x{valLo:X2}");
                else
                    LoadIntoW(arg.Operand);

                SelectBank(targetAddr);
                Emit("SUBWF", targetAddr, "F");
                SelectBank(targetAddrHi);
                Emit("BTFSS", "STATUS", "0");
                Emit("DECF", targetAddrHi, "F");

                if (isConst)
                {
                    Emit("MOVLW", $"0x{valHi:X2}");
                    Emit("SUBWF", targetAddrHi, "F");
                }
                else if (GetValSize(arg.Operand) >= 2)
                {
                    string opHi = HighByteOperand(ResolveAddress(arg.Operand));
                    SelectBank(opHi);
                    Emit("MOVF", opHi, "W");
                    SelectBank(targetAddrHi);
                    Emit("SUBWF", targetAddrHi, "F");
                }
                // 8-bit operand: high byte is zero — the borrow DECF above
                // already completed the subtract. Reading operand+1 would read
                // the NEXT variable's RAM slot (same bug fixed in Copy).

                return;
            }

            // 4./5. Shift 16-bit. __tmp (bank-0 counter) and the target can be
            // in different banks (e.g. `x <<= TRISC.value` loads the count with
            // bank 1 selected), so every access is bank-selected. The bank state
            // at the backedge equals the state at the loop head (bank of __tmp),
            // keeping the linear bank tracker consistent. Note the TARGET can
            // never live outside bank 0 today (the bump allocator is bank-0-only
            // and throws on overflow), but the SelectBank calls cost nothing in
            // that case and keep this correct if banked allocation ever lands.
            if (arg.Op == IrBinOp.LShift || arg.Op == IrBinOp.RShift)
            {
                bool left = arg.Op == IrBinOp.LShift;
                string loopLbl = MakeLabel(left ? "augls16" : "augrs16");
                string doneLbl = loopLbl + "_done";
                LoadIntoW(arg.Operand);
                GetOrAllocVariable("__tmp");
                SelectBank("__tmp");
                Emit("MOVWF", "__tmp");
                EmitLabel(loopLbl);
                Emit("MOVF", "__tmp", "F");
                Emit("BTFSC", "STATUS", "2");
                Emit("GOTO", doneLbl);
                Emit("BCF", "STATUS", "0");
                if (left)
                {
                    SelectBank(targetAddr);
                    Emit("RLF", targetAddr, "F");
                    SelectBank(targetAddrHi);
                    Emit("RLF", targetAddrHi, "F");
                }
                else
                {
                    SelectBank(targetAddrHi);
                    Emit("RRF", targetAddrHi, "F");
                    SelectBank(targetAddr);
                    Emit("RRF", targetAddr, "F");
                }

                SelectBank("__tmp");
                Emit("DECF", "__tmp", "F");
                Emit("GOTO", loopLbl);
                EmitLabel(doneLbl);
                return;
            }
        }

        throw new NotSupportedException("PIC14: AugAssign size/op not fully implemented");
    }

    private void EmitFloatAdd(string target, string source)
    {
        // Move 4 bytes of source to __FP_B
        for (int i = 0; i < 4; i++)
        {
            string srcByte = i == 0
                ? source
                : source.StartsWith("0x")
                    ? $"0x{ParseHexAddr(source) + i:X2}"
                    : source + "+" + i;
            SelectBank(srcByte);
            Emit("MOVF", srcByte, "W");
            Emit("MOVWF", $"__FP_B+{i}");
        }

        // Move 4 bytes of target to __FP_A
        for (int i = 0; i < 4; i++)
        {
            string tgtByte = i == 0
                ? target
                : target.StartsWith("0x")
                    ? $"0x{ParseHexAddr(target) + i:X2}"
                    : target + "+" + i;
            SelectBank(tgtByte);
            Emit("MOVF", tgtByte, "W");
            Emit("MOVWF", $"__FP_A+{i}");
        }

        Emit("CALL", "__add_float");

        // Move 4 bytes of __FP_A back to target
        for (int i = 0; i < 4; i++)
        {
            string tgtByte = i == 0
                ? target
                : target.StartsWith("0x")
                    ? $"0x{ParseHexAddr(target) + i:X2}"
                    : target + "+" + i;
            Emit("MOVF", $"__FP_A+{i}", "W");
            SelectBank(tgtByte);
            Emit("MOVWF", tgtByte);
        }
    }

    private void CompileVariant(InlineAsm arg)
    {
        _assembly.Add(PIC14AsmLine.MakeRaw(arg.Code));
    }

    private void CompileVariant(DebugLine arg)
    {
        if (!string.IsNullOrEmpty(arg.SourceFile))
            EmitComment($"{arg.SourceFile}:{arg.Line}: {arg.Text}");
        else
            EmitComment($"Line {arg.Line}: {arg.Text}");
    }

    private void CompileVariant(JumpIfEqual arg)
    {
        // if (src1 == src2) goto target
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            string src1 = ResolveAddress(arg.Src1);
            SelectBank(src1);
            Emit("MOVF", src1, "W");
            Emit("XORLW", $"0x{c2.Value:X2}");
            Emit("BTFSC", "STATUS", "2");
            Emit("GOTO", arg.Target);
            return;
        }

        // Generic fallback
        LoadIntoW(arg.Src2);
        string s1 = ResolveAddress(arg.Src1);
        SelectBank(s1);
        Emit("SUBWF", s1, "W");
        Emit("BTFSC", "STATUS", "2");
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(JumpIfNotEqual arg)
    {
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            string src1 = ResolveAddress(arg.Src1);
            SelectBank(src1);
            Emit("MOVF", src1, "W");
            Emit("XORLW", $"0x{c2.Value:X2}");
            Emit("BTFSS", "STATUS", "2");
            Emit("GOTO", arg.Target);
            return;
        }

        LoadIntoW(arg.Src2);
        string s1 = ResolveAddress(arg.Src1);
        SelectBank(s1);
        Emit("SUBWF", s1, "W");
        Emit("BTFSS", "STATUS", "2");
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(JumpIfLessThan arg)
    {
        // if (src1 < src2) goto target
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            int k = c2.Value;
            if (k == 0) return; // Unsigned < 0 is never true
            string src1 = ResolveAddress(arg.Src1);
            SelectBank(src1);
            Emit("MOVF", src1, "W");
            Emit("SUBLW", $"0x{k - 1:X2}");
            Emit("BTFSC", "STATUS", "0");
            Emit("GOTO", arg.Target);
            return;
        }

        LoadIntoW(arg.Src2);
        string s1 = ResolveAddress(arg.Src1);
        SelectBank(s1);
        Emit("SUBWF", s1, "W");
        Emit("BTFSS", "STATUS", "0");
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(JumpIfLessOrEqual arg)
    {
        // if (src1 <= src2) goto target
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            int k = c2.Value;
            string src1 = ResolveAddress(arg.Src1);
            SelectBank(src1);
            Emit("MOVF", src1, "W");
            Emit("SUBLW", $"0x{k:X2}");
            Emit("BTFSC", "STATUS", "0");
            Emit("GOTO", arg.Target);
            return;
        }

        // Generic: reverse operands — src2 - src1 >= 0 -> src1 <= src2
        LoadIntoW(arg.Src1);
        string src2Addr = ResolveAddress(arg.Src2);
        SelectBank(src2Addr);
        Emit("SUBWF", src2Addr, "W");
        Emit("BTFSC", "STATUS", "0");
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(JumpIfGreaterThan arg)
    {
        // if (src1 > src2) goto target
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            int k = c2.Value;
            string src1 = ResolveAddress(arg.Src1);
            SelectBank(src1);
            Emit("MOVF", src1, "W");
            Emit("SUBLW", $"0x{k:X2}");
            Emit("BTFSS", "STATUS", "0");
            Emit("GOTO", arg.Target);
            return;
        }

        // Generic: flip operands — src2 - src1 < 0 -> C=0 -> src1 > src2
        LoadIntoW(arg.Src1);
        string src2Addr = ResolveAddress(arg.Src2);
        SelectBank(src2Addr);
        Emit("SUBWF", src2Addr, "W");
        Emit("BTFSS", "STATUS", "0");
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(JumpIfGreaterOrEqual arg)
    {
        // if (src1 >= src2) goto target
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            int k = c2.Value;
            if (k == 0)
            {
                Emit("GOTO", arg.Target);
                return;
            }

            string src1 = ResolveAddress(arg.Src1);
            SelectBank(src1);
            Emit("MOVF", src1, "W");
            Emit("SUBLW", $"0x{k - 1:X2}");
            Emit("BTFSS", "STATUS", "0");
            Emit("GOTO", arg.Target);
            return;
        }

        LoadIntoW(arg.Src2);
        string s1 = ResolveAddress(arg.Src1);
        SelectBank(s1);
        Emit("SUBWF", s1, "W");
        Emit("BTFSC", "STATUS", "0");
        Emit("GOTO", arg.Target);
    }
}