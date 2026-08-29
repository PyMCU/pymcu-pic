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

namespace PyMCU.Backend.Targets.PIC18;

public class PIC18CodeGen : CodeGen
    {
        private const int StackBase = 0x060;

        private readonly Dictionary<string, List<string>> functionParams = new();

        private const int HighPriorityVector = 0x0008;
        private const int LowPriorityVector = 0x0018;
        private const int StartStubAddr = 0x0020;

        private int bssSize;

        private Function? interruptHandler;
        private Function? currentFunction;
        private int accessScratchHead;

        private readonly DeviceConfig config;
        private List<PIC18AsmLine> assembly = new();
        private Dictionary<string, int> symbolTable = new();
        private Dictionary<string, int> stackLayout = new();
        private Dictionary<string, int> varSizes = new();
        private int ramHead;
        private int labelCounter;
        private int currentBank = -1;

        public PIC18CodeGen(DeviceConfig cfg)
        {
            config = cfg;
            ramHead = StackBase; // Start after Access Bank
        }

        public override void EmitContextSave()
        {
        }

        public override void EmitContextRestore()
        {
        }

        public override void EmitInterruptReturn() => Emit("RETFIE", "FAST");

        private string MakeLabel(string prefix) => $"{prefix}_{labelCounter++}";

        private void Emit(string m) => assembly.Add(PIC18AsmLine.MakeInstruction(m));
        private void Emit(string m, string o1) => assembly.Add(PIC18AsmLine.MakeInstruction(m, o1));
        private void Emit(string m, string o1, string o2) => assembly.Add(PIC18AsmLine.MakeInstruction(m, o1, o2));

        private void Emit(string m, string o1, string o2, string o3) =>
            assembly.Add(PIC18AsmLine.MakeInstruction(m, o1, o2, o3));

        private void EmitLabel(string l)
        {
            assembly.Add(PIC18AsmLine.MakeLabel(l));
            currentBank = -1;
        }

        private void EmitComment(string c) => assembly.Add(PIC18AsmLine.MakeComment(c));
        private void EmitRaw(string t) => assembly.Add(PIC18AsmLine.MakeRaw(t));

        private string GetOrAllocVariable(string name)
        {
            if (!symbolTable.ContainsKey(name)) symbolTable[name] = ramHead++;
            return name;
        }

        private readonly Dictionary<string, int> backendScratchBlocks = new();
        private readonly Dictionary<string, (int Start, int Count)> functionRanges = new();

        private string AllocAccessScratch(string name, int size = 1, bool backendOwned = true)
        {
            if (backendOwned) backendScratchBlocks[name] = size;
            if (symbolTable.ContainsKey(name)) return name;
            if (accessScratchHead + size > StackBase)
                throw new InvalidOperationException(
                    "PIC18 backend: access-RAM scratch cells overflow the window " +
                    $"(0x000-0x{StackBase - 1:X3}); '{name}' does not fit.");
            symbolTable[name] = accessScratchHead;
            accessScratchHead += size;
            return name;
        }

        private int GetAddress(string operand)
        {
            if (string.IsNullOrEmpty(operand)) return -1;
            if (operand.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return Convert.ToInt32(operand, 16);
                }
                catch
                {
                    return -1;
                }
            }

            if (symbolTable.TryGetValue(operand, out int sa)) return sa;
            if (stackLayout.TryGetValue(operand, out int sl)) return 0x60 + sl;

            int plus = operand.IndexOf('+');
            if (plus > 0 && int.TryParse(operand.AsSpan(plus + 1), out int delta))
            {
                int baseAddr = GetAddress(operand.Substring(0, plus));
                if (baseAddr != -1) return baseAddr + delta;
            }

            return -1;
        }

        private string GetAccessMode(string operand)
        {
            int addr = GetAddress(operand);
            if (addr == -1) return "ACCESS";
            if (addr <= 0x5F || addr >= 0xF60) return "ACCESS";
            return "BANKED";
        }

        private void SelectBank(string operand)
        {
            int addr = GetAddress(operand);
            if (addr == -1) return;
            if (addr <= 0x5F || addr >= 0xF60) return;
            int newBank = (addr >> 8) & 0x0F;
            if (currentBank == newBank) return;
            Emit("MOVLB", $"0x{newBank:X2}");
            currentBank = newBank;
        }

        private string ResolveAddress(Val val)
        {
            if (val is NoneVal) return "";
            if (val is Constant c) return $"0x{c.Value & 0xFF:X2}";
            if (val is MemoryAddress mem) return $"0x{mem.Address:X2}";
            string name = val switch { Variable v => v.Name, Temporary t => t.Name, _ => "" };
            if (string.IsNullOrEmpty(name)) return "";
            if (stackLayout.ContainsKey(name)) return name;
            return GetOrAllocVariable(name);
        }

        private static DataType GetValType(Val val) => val switch
        {
            FloatConstant => DataType.FLOAT,
            Variable v => v.Type,
            Temporary t => t.Type,
            MemoryAddress m => m.Type,
            ArrayBase => DataType.UINT16,
            FlashStrAddr => DataType.UINT16,
            Constant { Value: > 65535 } => DataType.UINT32,
            Constant { Value: < -32768 } => DataType.INT32,
            Constant { Value: > 255 or < -128 } => DataType.UINT16,
            _ => DataType.UINT8,
        };

        private int WidthOf(Val val)
        {
            string name = val switch { Variable v => v.Name, Temporary t => t.Name, _ => "" };
            if (name.Length > 0 && varSizes.TryGetValue(name, out int slot) && slot > 0) return slot;
            return Math.Max(GetValType(val).SizeOf(), 1);
        }

        private static bool IsSignedComparison(Val src1, Val src2)
        {
            if (GetValType(src1).IsSigned() || GetValType(src2).IsSigned()) return true;
            if (src1 is Constant c1 && c1.Value < 0) return true;
            if (src2 is Constant c2 && c2.Value < 0) return true;
            return false;
        }

        private string ByteOf(Val val, int byteOffset)
        {
            string addr = ResolveAddress(val);
            if (byteOffset == 0) return addr;
            if (addr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return $"0x{GetAddress(addr) + byteOffset:X3}";
            return $"{addr}+{byteOffset}";
        }

        private void LoadByteIntoW(Val val, int byteOffset)
        {
            if (val is Constant c)
            {
                Emit("MOVLW", $"0x{(c.Value >> (8 * byteOffset)) & 0xFF:X2}");
                return;
            }

            string addr = ByteOf(val, byteOffset);
            SelectBank(addr);
            Emit("MOVF", addr, "W", GetAccessMode(addr));
        }

        private const string ReturnHigh = "__ret_hi";
        private const string FloatA = "__fp_a";
        private const string FloatB = "__fp_b";
        private const string FloatScratch = "__fp_s";

        private bool usesFloat;

        private const string MulResult = "__mul_r";
        private const string MulStage = "__mul_b";
        private const string ErrCell = "__err";
        private const string ErrTrapLabel = "__err_trap";
        private const string UnhandledExnLabel = "__pymcu_unhandled_exn";

        private bool needsErrTrap;

        private const string MultiByteTmp = "__mb_tmp";
        private const string MultiByteAcc = "__mb_acc";

        private void LoadExtendedByteIntoW(Val val, int index)
        {
            if (val is ArrayBase ab)
            {
                string name = ArrayBaseOperand(ab.ArrayName);
                Emit("MOVLW", index switch { 0 => $"LOW({name})", 1 => $"HIGH({name})", _ => "0x00" });
                return;
            }

            if (val is FlashStrAddr fsa)
            {
                string tbl = FlashLabel(fsa.Name);
                Emit("MOVLW", index switch { 0 => $"LOW({tbl})", 1 => $"HIGH({tbl})", _ => "0x00" });
                return;
            }

            if (val is FloatConstant fc)
            {
                uint bits = BitConverter.SingleToUInt32Bits((float)fc.Value);
                Emit("MOVLW", $"0x{(bits >> (8 * index)) & 0xFF:X2}");
                return;
            }

            if (val is Constant c)
            {
                Emit("MOVLW", $"0x{(c.Value >> (8 * index)) & 0xFF:X2}");
                return;
            }

            int width = WidthOf(val);
            if (index < width)
            {
                LoadByteIntoW(val, index);
                return;
            }

            if (!GetValType(val).IsSigned())
            {
                Emit("MOVLW", "0x00");
                return;
            }

            string top = ByteOf(val, width - 1);
            SelectBank(top);
            Emit("MOVLW", "0x00");
            Emit("BTFSC", top, "7", GetAccessMode(top));
            Emit("MOVLW", "0xFF");
        }

        private void StoreWIntoByte(Val val, int index)
        {
            string addr = ByteOf(val, index);
            SelectBank(addr);
            Emit("MOVWF", addr, GetAccessMode(addr));
        }

        private static string WideMnemonic(PyMCU.IR.BinaryOp op, bool first) => op switch
        {
            PyMCU.IR.BinaryOp.Add => first ? "ADDWF" : "ADDWFC",
            PyMCU.IR.BinaryOp.Sub => first ? "SUBWF" : "SUBWFB",
            PyMCU.IR.BinaryOp.BitAnd => "ANDWF",
            PyMCU.IR.BinaryOp.BitOr => "IORWF",
            PyMCU.IR.BinaryOp.BitXor => "XORWF",
            _ => throw new NotSupportedException($"PIC18: {op} has no multi-byte form"),
        };

        private static bool IsWideArithmetic(PyMCU.IR.BinaryOp op) =>
            op is PyMCU.IR.BinaryOp.Add or PyMCU.IR.BinaryOp.Sub or PyMCU.IR.BinaryOp.BitAnd
                or PyMCU.IR.BinaryOp.BitOr or PyMCU.IR.BinaryOp.BitXor;

        private void CompileWideArithmetic(Binary arg, int width)
        {
            string tmp = AllocAccessScratch(MultiByteTmp);
            bool isSub = arg.Op == PyMCU.IR.BinaryOp.Sub;

            for (int i = 0; i < width; ++i)
            {
                LoadExtendedByteIntoW(isSub ? arg.Src1 : arg.Src2, i);
                Emit("MOVWF", tmp, GetAccessMode(tmp));
                LoadExtendedByteIntoW(isSub ? arg.Src2 : arg.Src1, i);
                Emit(WideMnemonic(arg.Op, i == 0), tmp, "W", GetAccessMode(tmp));
                StoreWIntoByte(arg.Dst, i);
            }
        }

        private void EmitWidePassRight(Val dst, int width, bool arithmetic)
        {
            if (arithmetic)
            {
                string top = ByteOf(dst, width - 1);
                SelectBank(top);
                Emit("BCF", "STATUS", "C", "ACCESS");
                Emit("BTFSC", top, "7", GetAccessMode(top));
                Emit("BSF", "STATUS", "C", "ACCESS");
            }
            else Emit("BCF", "STATUS", "C", "ACCESS");

            for (int i = width - 1; i >= 0; --i)
            {
                string b = ByteOf(dst, i);
                SelectBank(b);
                Emit("RRCF", b, "F", GetAccessMode(b));
            }
        }

        private void EmitWidePassLeft(Val dst, int width)
        {
            Emit("BCF", "STATUS", "C", "ACCESS");
            for (int i = 0; i < width; ++i)
            {
                string b = ByteOf(dst, i);
                SelectBank(b);
                Emit("RLCF", b, "F", GetAccessMode(b));
            }
        }

        private void CompileWideShift(Binary arg, int width, bool left, bool arithmetic)
        {
            if (arg.Src2 is Constant c)
            {
                int amount = Math.Max(c.Value, 0);
                if (arithmetic) amount = Math.Min(amount, 8 * width - 1);
                int byteOffset = amount / 8;
                int bitShift = amount % 8;

                if (left)
                    for (int i = width - 1; i >= 0; --i)
                    {
                        if (i >= byteOffset) LoadExtendedByteIntoW(arg.Src1, i - byteOffset);
                        else Emit("MOVLW", "0x00");
                        StoreWIntoByte(arg.Dst, i);
                    }
                else
                    for (int i = 0; i < width; ++i)
                    {
                        LoadExtendedByteIntoW(arg.Src1, i + byteOffset);
                        StoreWIntoByte(arg.Dst, i);
                    }

                for (int k = 0; k < bitShift; ++k)
                {
                    if (left) EmitWidePassLeft(arg.Dst, width);
                    else EmitWidePassRight(arg.Dst, width, arithmetic);
                }

                return;
            }

            string count = GetOrAllocVariable(MakeLabel("shift_count"));
            string lblLoop = MakeLabel("shift_loop");
            string lblEnd = MakeLabel("shift_end");

            LoadIntoW(arg.Src2);
            SelectBank(count);
            Emit("MOVWF", count, GetAccessMode(count));
            for (int i = 0; i < width; ++i)
            {
                LoadExtendedByteIntoW(arg.Src1, i);
                StoreWIntoByte(arg.Dst, i);
            }

            SelectBank(count);
            Emit("MOVF", count, "F", GetAccessMode(count));
            Emit("BZ", lblEnd);
            EmitLabel(lblLoop);
            if (left) EmitWidePassLeft(arg.Dst, width);
            else EmitWidePassRight(arg.Dst, width, arithmetic);
            SelectBank(count);
            Emit("DECFSZ", count, "F", GetAccessMode(count));
            Emit("BRA", lblLoop);
            EmitLabel(lblEnd);
        }

        private static bool IsArithmeticShift(PyMCU.IR.BinaryOp op, Val src1) =>
            op == PyMCU.IR.BinaryOp.RShift
            && (GetValType(src1).IsSigned() || (src1 is Constant c && c.Value < 0));

        private void LoadIntoW(Val val)
        {
            if (val is Constant c)
            {
                Emit("MOVLW", $"0x{c.Value & 0xFF:X2}");
            }
            else
            {
                string addr = ResolveAddress(val);
                SelectBank(addr);
                Emit("MOVF", addr, "W", GetAccessMode(addr));
            }
        }

        private void StoreWInto(Val val)
        {
            string addr = ResolveAddress(val);
            SelectBank(addr);
            Emit("MOVWF", addr, GetAccessMode(addr));
        }

        private static readonly Regex AsmLabelDef =
            new(@"^\s*(_[A-Za-z_][A-Za-z0-9_]*):", RegexOptions.Compiled);
        private static readonly Regex AsmScratchRef =
            new(@"\b__[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.Compiled);
        private static readonly Regex AsmCodeRef =
            new(@"\b(?:CALL|GOTO|BRA|RCALL)\s+(__[A-Za-z_][A-Za-z0-9_]*)\b",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private int asmRunCounter;

        // One expansion of an `asm()` block is one run, and the labels it defines are renamed
        // so a body inlined twice does not define the same label twice.
        //
        // A run used to mean ADJACENT InlineAsm instructions. Since PyMCU 0ea529e4 a DebugLine
        // sits between every `asm()` of an inlined body, so every run held exactly one
        // instruction: `_dly_i16m18:` was renamed inside its own run and the `BRA _dly_i16m18`
        // in the next run kept the old name. gpasm: Error[113] Symbol not previously defined.
        // The whole PIC corpus stopped assembling, `blink` on every chip of all three families.
        //
        // A DebugLine emits a comment and no code, so it cannot separate two halves of one asm
        // block. Anything that emits code can, and still ends the run.
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
                        renames[m.Groups[1].Value] = $"{m.Groups[1].Value}_{asmRunCounter}";
                }
                if (renames.Count > 0) asmRunCounter++;

                for (int j = start; j < end; j++)
                {
                    if (body[j] is not InlineAsm ia) { result.Add(body[j]); continue; }
                    var code = ia.Code;
                    foreach (var (oldName, newName) in renames)
                        code = Regex.Replace(code, $@"\b{Regex.Escape(oldName)}\b", newName);
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
                foreach (Match m in AsmCodeRef.Matches(ia.Code))
                    codeRefs.Add(m.Groups[1].Value);
                foreach (Match m in AsmScratchRef.Matches(ia.Code))
                    referenced.Add(m.Value);
            }

            foreach (var name in referenced.Distinct())
            {
                if (definedLabels.Contains(name) || codeRefs.Contains(name)) continue;
                if (name.StartsWith("__FP_")) continue;
                if (stackLayout.ContainsKey(name) || symbolTable.ContainsKey(name)) continue;
                AllocAccessScratch(name, 1, backendOwned: false);
                EmitRaw($"{name} EQU 0x{symbolTable[name]:X3}");
            }
        }

        // GPR space is linear from _stack_base upward; the SFRs start where the
        // header's __BADRAM does. gpasm downgrades an out-of-range EQU to a warning
        // and still writes a hex, so without this a program that does not fit
        // "builds" and then writes where there is no memory.
        private static readonly Dictionary<string, int> RamEndExclusive =
            new(StringComparer.OrdinalIgnoreCase) { ["pic18f45k50"] = 0x800 };

        private void RejectRamOverflow()
        {
            if (!RamEndExclusive.TryGetValue(ChipName(), out int end)) return;

            foreach (var (name, addr) in symbolTable)
                if (addr >= end)
                    throw new InvalidOperationException(
                        $"PIC18CodeGen: '{name}' was placed at 0x{addr:X3}, past the end of the " +
                        $"GPR space of {ChipName()} (0x000-0x{end - 1:X3}). That address is not " +
                        "data memory and the write would be discarded.");

            if (ramHead <= end) return;
            throw new InvalidOperationException(
                $"PIC18CodeGen: this program needs {ramHead - StackBase} bytes of data RAM but " +
                $"{ChipName()} has {end - StackBase} (0x{StackBase:X3}-0x{end - 1:X3}). Reduce " +
                "variables or pick a chip with more RAM.");
        }

        private string ChipName()
        {
            if (!string.IsNullOrEmpty(config.TargetChip)) return config.TargetChip;
            if (!string.IsNullOrEmpty(config.Chip)) return config.Chip;
            return config.DetectedChip;
        }

        private static readonly Dictionary<string, (string Key, string Val)[]> DefaultFuses =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["pic18f45k50"] =
                [
                    ("FOSC", "INTOSCIO"), ("PLLSEL", "PLL3X"), ("CFGPLLEN", "OFF"),
                    ("CPUDIV", "NOCLKDIV"), ("LS48MHZ", "SYS24X4"), ("PCLKEN", "OFF"),
                    ("FCMEN", "OFF"), ("IESO", "OFF"), ("PWRTEN", "OFF"),
                    ("BOREN", "OFF"), ("LPBOR", "ON"), ("WDTEN", "OFF"),
                    ("MCLRE", "ON"), ("STVREN", "ON"), ("LVP", "OFF"),
                    ("XINST", "OFF"), ("DEBUG", "OFF"),
                ],
            };

        private void EmitOscillatorInit()
        {
            if (!DefaultFuses.ContainsKey(ChipName())) return;

            int ircf = config.Frequency switch
            {
                >= 16_000_000 => 0x70,
                >= 8_000_000 => 0x60,
                >= 4_000_000 => 0x50,
                >= 2_000_000 => 0x40,
                _ => 0x30,
            };

            Emit("MOVLW", $"0x{ircf | 0x03:X2}");
            Emit("MOVWF", "OSCCON", "ACCESS");
        }

        private void EmitConfigDirectives()
        {
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, val) in config.Fuses)
            {
                EmitRaw($"\tCONFIG {key} = {val}");
                emitted.Add(key);
            }

            if (!DefaultFuses.TryGetValue(ChipName(), out var defaults))
            {
                if (emitted.Count == 0) PicDiagnostics.WarnNoConfigWord(ChipName());
                return;
            }

            foreach (var (key, val) in defaults)
                if (emitted.Add(key))
                    EmitRaw($"\tCONFIG {key} = {val}");
        }

        private static bool IsFloatVal(Val v) => v is FloatConstant || GetValType(v) == DataType.FLOAT;

        private static string? FloatGapReason(Instruction instr) => instr switch
        {
            Binary b when IsRelational(b.Op) => null,
            Binary { Op: PyMCU.IR.BinaryOp.Add or PyMCU.IR.BinaryOp.Sub
                or PyMCU.IR.BinaryOp.Mul or PyMCU.IR.BinaryOp.Div
                or PyMCU.IR.BinaryOp.FloorDiv } => null,
            JumpIfLessThan or JumpIfLessOrEqual or JumpIfGreaterThan
                or JumpIfGreaterOrEqual or JumpIfEqual or JumpIfNotEqual => null,
            Binary b when IsFloatVal(b.Src1) || IsFloatVal(b.Src2) || IsFloatVal(b.Dst) =>
                $"the '{b.Op}' operator has no floating-point form",
            Unary u when u.Op != PyMCU.IR.UnaryOp.Neg
                && (IsFloatVal(u.Src) || IsFloatVal(u.Dst)) =>
                $"the unary '{u.Op}' operator has no floating-point form",
            AugAssign a when IsFloatVal(a.Target) || IsFloatVal(a.Operand) =>
                "augmented assignment on a floating-point value has no lowering",
            _ => null,
        };

        private static void RejectFloats(ProgramIR program)
        {
            foreach (var func in program.Functions)
            foreach (var instr in func.Body)
            {
                string? reason = FloatGapReason(instr);
                if (reason is not null)
                    throw new NotSupportedException(
                        $"PIC18CodeGen: {reason} on this backend. Floating-point arithmetic, " +
                        "comparison, negation and conversion are implemented; only this case " +
                        "would be truncated to an integer silently.");
            }
        }

        private static void RejectExceptions(ProgramIR program)
        {
            foreach (var func in program.Functions)
            foreach (var instr in func.Body)
                if (instr is Call { FunctionName: UnhandledExnLabel })
                    throw new NotSupportedException(
                        "PIC18CodeGen: runtime exceptions (try/except, and a raise that survives to " +
                    "run time) are not implemented yet for the PIC18 " +
                        "backend. The error-propagation runtime (__pymcu_unhandled_exn) has no PIC " +
                        "definition, so the program would otherwise fail with an opaque gpasm " +
                        "\"Symbol not previously defined\" error at assembly time.");
        }

        public override void Compile(ProgramIR program, TextWriter output)
        {
            RejectExceptions(program);
            RejectFloats(program);
            assembly.Clear();
            currentBank = -1;

            functionParams.Clear();
            foreach (var func in program.Functions)
                functionParams[func.Name] = func.Params;

            foreach (var func in program.Functions)
                func.Body = UniquifyInlineAsmLabels(func.Body);

            accessScratchHead = 0x000;
            usesFloat = false;
            flashData.Clear();
            flashDataNames.Clear();
            divWidths.Clear();
            backendScratchBlocks.Clear();
            functionRanges.Clear();
            needsErrTrap = false;
            bssSize = program.Globals.Sum(g => g.Type.SizeOf()) + program.GlobalArrays.Values.Sum();

            interruptHandler = null;
            foreach (var func in program.Functions)
            {
                if (!func.IsInterrupt) continue;
                if (interruptHandler != null)
                    throw new NotSupportedException(
                        "PIC18 backend: only one @interrupt handler is supported (IPEN = 0 routes " +
                        $"every source to the 0x{HighPriorityVector:X4} vector); found '{interruptHandler.Name}' " +
                        $"and '{func.Name}'.");
                interruptHandler = func;
            }

            var allocator = new StackAllocator();
            var (offsets, maxStack) = allocator.Allocate(program);
            stackLayout = offsets;
            varSizes = allocator.VariableSizes;
            ramHead = 0x60 + maxStack;

            // 1. Compile functions first to populate symbol_table
            foreach (var func in program.Functions)
                CompileFunction(func);

            RejectRamOverflow();
            ProtectInterruptScratch(program);
            var functionCode = new List<PIC18AsmLine>(assembly);
            assembly.Clear();

            // 2. Emit Headers
            string chipName = ChipName();
            string chipFile = chipName.ToLowerInvariant();
            string listP = chipName;
            if (listP.Length > 3 && listP.StartsWith("pic", StringComparison.OrdinalIgnoreCase))
                listP = listP.Substring(3);
            listP = listP.ToUpperInvariant();
            EmitRaw($"\tLIST P={listP}");

            if (chipFile.StartsWith("pic"))
                chipFile = "p" + chipFile.Substring(3);
            EmitRaw($"\t#include <{chipFile}.inc>");
            EmitConfigDirectives();

            EmitRaw($"_stack_base EQU 0x{StackBase:X3}");
            if (bssSize > 0) EmitRaw($"_bss_end EQU _stack_base + 0x{bssSize:X3}");
            foreach (var (name, offset) in stackLayout)
                EmitRaw($"{name} EQU _stack_base + 0x{offset:X3}");

            // 3. Emit Global Variables
            foreach (var (name, addr) in symbolTable)
                EmitRaw($"{name} EQU 0x{addr:X3}");

            EmitInlineAsmScratchSymbols(program);

            EmitRaw("    ORG 0x0000");
            Emit("GOTO", "_pymcu_start");

            EmitRaw($"    ORG 0x{HighPriorityVector:X4}");
            if (interruptHandler != null) Emit("GOTO", interruptHandler.Name);
            else Emit("RETFIE");

            EmitRaw($"    ORG 0x{LowPriorityVector:X4}");
            Emit("RETFIE");

            EmitRaw($"    ORG 0x{StartStubAddr:X4}");
            EmitLabel("_pymcu_start");
            EmitOscillatorInit();
            Emit("GOTO", "main");

            // 4. Append function code
            assembly.AddRange(functionCode);
            if (usesFloat) EmitRaw("#include \"float.inc\"");
            EmitErrorTrap();
            EmitDivisionRoutines();
            EmitFlashTables();

            // Run peephole
            var optimized = PIC18Peephole.Optimize(assembly);

            // Then choose BRA against GOTO by distance. After the peephole, because it adds and
            // removes instructions and a distance measured before it is measured on code that
            // is not what gets assembled.
            PIC18BranchRelax.Apply(optimized);

            foreach (var line in optimized)
                output.WriteLine(line.ToString());

            output.WriteLine("\tEND");
        }

        // Same contract as the AVR backend: zero the globals region on entry to
        // main, before the IR's own initializer stores run. Globals with an
        // initializer arrive as ordinary Copy instructions; the ones without were
        // reading whatever the RAM powered up with.
        private void EmitBssClear()
        {
            if (bssSize <= 0) return;

            string loop = MakeLabel("bss_loop");
            string body = MakeLabel("bss_body");
            string done = MakeLabel("bss_done");

            Emit("LFSR", "0", "_stack_base");
            EmitLabel(loop);
            Emit("MOVLW", "LOW(_bss_end)");
            Emit("XORWF", "FSR0L", "W", "ACCESS");
            Emit("BNZ", body);
            Emit("MOVLW", "HIGH(_bss_end)");
            Emit("XORWF", "FSR0H", "W", "ACCESS");
            Emit("BZ", done);
            EmitLabel(body);
            Emit("CLRF", "POSTINC0", "ACCESS");
            Emit("BRA", loop);
            EmitLabel(done);
        }

        private void CompileFunction(Function func)
        {
            currentFunction = func;
            int start = assembly.Count;
            EmitLabel(func.Name);
            if (func.Name == "main") EmitBssClear();
            foreach (var instr in func.Body)
                CompileInstruction(instr);

            if (func.IsInterrupt && assembly[^1].Mnemonic != "RETFIE") EmitInterruptReturn();
            functionRanges[func.Name] = (start, assembly.Count - start);
            currentFunction = null;
        }

        private static IEnumerable<string> CalleesOf(Function func)
        {
            foreach (var instr in func.Body)
                switch (instr)
                {
                    case Call c: yield return c.FunctionName; break;
                    case VirtualCall v: yield return $"{v.DefiningClass}_{v.MethodName}"; break;
                }
        }

        private static bool HasIndirectCall(Function func) =>
            func.Body.Any(i => i is IndirectCall);

        private void ProtectInterruptScratch(ProgramIR program)
        {
            if (interruptHandler == null || backendScratchBlocks.Count == 0) return;

            var byName = program.Functions.ToDictionary(f => f.Name, f => f);
            var reached = new HashSet<string>();
            var pending = new Stack<string>();
            pending.Push(interruptHandler.Name);
            bool opaque = false;

            while (pending.Count > 0)
            {
                string name = pending.Pop();
                if (!reached.Add(name) || !byName.TryGetValue(name, out var func)) continue;
                if (HasIndirectCall(func)) opaque = true;
                foreach (string callee in CalleesOf(func)) pending.Push(callee);
            }

            var text = new System.Text.StringBuilder();
            foreach (string name in reached)
            {
                if (!functionRanges.TryGetValue(name, out var range)) continue;
                for (int i = range.Start; i < range.Start + range.Count; ++i)
                    text.AppendLine(assembly[i].ToString());
            }

            string body = text.ToString();
            var used = backendScratchBlocks
                .Where(kv => opaque || Regex.IsMatch(body, $@"\b{Regex.Escape(kv.Key)}\b"))
                .OrderBy(kv => kv.Key)
                .Select(kv => (Name: kv.Key, Size: kv.Value))
                .ToList();

            if (used.Count == 0) return;

            var (isrStart, isrCount) = functionRanges[interruptHandler.Name];
            var isrBody = assembly.GetRange(isrStart, isrCount);
            var rebuilt = new List<PIC18AsmLine> { isrBody[0] };

            foreach (var (name, size) in used)
            {
                AllocAccessScratch(name + "_s", size, backendOwned: false);
                for (int i = 0; i < size; ++i)
                    rebuilt.Add(PIC18AsmLine.MakeInstruction(
                        "MOVFF", Cell(name, i), Cell(name + "_s", i)));
            }

            for (int k = 1; k < isrBody.Count; ++k)
            {
                if (isrBody[k].Mnemonic == "RETFIE")
                    foreach (var (name, size) in used)
                        for (int i = 0; i < size; ++i)
                            rebuilt.Add(PIC18AsmLine.MakeInstruction(
                                "MOVFF", Cell(name + "_s", i), Cell(name, i)));
                rebuilt.Add(isrBody[k]);
            }

            assembly.RemoveRange(isrStart, isrCount);
            assembly.InsertRange(isrStart, rebuilt);
        }

        private void CompileInstruction(Instruction instr)
        {
            switch (instr)
            {
                case Return arg: CompileReturn(arg); break;
                case Copy arg: CompileCopy(arg); break;
                case Unary arg: CompileUnary(arg); break;
                case Binary arg: CompileBinary(arg); break;
                case Jump arg: Emit("BRA", arg.Target); break;
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
                case LoadIndirect arg: CompileLoadIndirect(arg); break;
                case StoreIndirect arg: CompileStoreIndirect(arg); break;
                case SignalError arg: CompileSignalError(arg); break;
                case SignalSuccess: CompileSignalSuccess(); break;
                case BranchOnError arg: CompileBranchOnError(arg); break;
                case InlineAsm arg: assembly.Add(PIC18AsmLine.MakeRaw(arg.Code)); break;
                case Bitcast arg: CompileBitcast(arg); break;
                case InlineExpansionMarker:
                case GcRoot:
                case GcUnroot:
                    break;
                case IndirectCall:
                case VirtualCall:
                    throw new NotSupportedException(
                        $"PIC18CodeGen: {instr.GetType().Name} (calls through a function pointer or " +
                        "vtable) is not implemented yet for the PIC18 backend. The call would " +
                        "otherwise be dropped silently.");
                case DebugLine arg:
                    if (!string.IsNullOrEmpty(arg.SourceFile)) EmitComment($"{arg.SourceFile}:{arg.Line}: {arg.Text}");
                    else EmitComment($"Line {arg.Line}: {arg.Text}");
                    break;
                case ArrayLoad arg: CompileArrayLoad(arg); break;
                case ArrayStore arg: CompileArrayStore(arg); break;
                case BytearrayLoad arg: CompileBytearrayLoad(arg); break;
                case BytearrayStore arg: CompileBytearrayStore(arg); break;
                case FlashData arg: CompileFlashData(arg); break;
                case ArrayLoadFlash arg: CompileArrayLoadFlash(arg); break;
                case FlashLoadPtr arg: CompileFlashLoadPtr(arg); break;
                default:
                    throw new NotSupportedException(
                        $"PIC18CodeGen: unhandled IR instruction '{instr.GetType().Name}'. " +
                        "Add an explicit case (or a documented no-op) for it; falling through this " +
                        "switch drops the instruction silently and miscompiles the program.");
            }
        }

        private int ReturnWidth(Val value)
        {
            var declared = currentFunction?.ReturnType ?? DataType.UNKNOWN;
            int width = declared is DataType.VOID or DataType.UNKNOWN
                ? WidthOf(value)
                : Math.Max(declared.SizeOf(), 1);
            return Math.Min(width, 4);
        }

        private void CompileReturn(Return arg)
        {
            if (currentFunction?.IsInterrupt == true)
            {
                EmitInterruptReturn();
                return;
            }

            if (arg.Value is not NoneVal)
            {
                int width = ReturnWidth(arg.Value);
                if (width > 1)
                {
                    AllocAccessScratch(ReturnHigh, 3);
                    for (int i = 1; i < width; ++i)
                    {
                        LoadExtendedByteIntoW(arg.Value, i);
                        Emit("MOVWF", Cell(ReturnHigh, i - 1), GetAccessMode(ReturnHigh));
                    }

                    LoadExtendedByteIntoW(arg.Value, 0);
                }
                else LoadIntoW(arg.Value);
            }

            Emit("RETURN");
        }

        private void CompileFloatConvert(Copy arg, bool toFloat)
        {
            AllocFloatScratch();
            usesFloat = true;

            for (int i = 0; i < 4; ++i)
            {
                LoadExtendedByteIntoW(arg.Src, i);
                Emit("MOVWF", Cell(FloatA, i), GetAccessMode(FloatA));
            }

            Emit("CALL", toFloat ? "__fp_float" : "__fp_fix");

            int dstWidth = toFloat ? 4 : WidthOf(arg.Dst);
            for (int i = 0; i < dstWidth && i < 4; ++i)
            {
                Emit("MOVF", Cell(FloatA, i), "W", GetAccessMode(FloatA));
                if (dstWidth == 1) StoreWInto(arg.Dst);
                else StoreWIntoByte(arg.Dst, i);
            }
        }

        private void CompileBitcast(Bitcast bc)
        {
            int dstWidth = WidthOf(bc.Dst);
            int srcWidth = WidthOf(bc.Src);
            for (int i = 0; i < dstWidth; ++i)
            {
                if (i < srcWidth) LoadExtendedByteIntoW(bc.Src, i);
                else Emit("MOVLW", "0x00");
                StoreWIntoByte(bc.Dst, i);
            }
        }

        private void CompileCopy(Copy arg)
        {
            bool srcFloat = IsFloatVal(arg.Src);
            bool dstFloat = IsFloatVal(arg.Dst);
            if (srcFloat != dstFloat && arg.Src is not FloatConstant)
            {
                CompileFloatConvert(arg, dstFloat);
                return;
            }

            int width = WidthOf(arg.Dst);
            if (width > 1)
            {
                for (int i = 0; i < width; ++i)
                {
                    LoadExtendedByteIntoW(arg.Src, i);
                    StoreWIntoByte(arg.Dst, i);
                }

                return;
            }

            if (arg.Src is Constant)
            {
                LoadIntoW(arg.Src);
                StoreWInto(arg.Dst);
            }
            else
            {
                string src = ResolveAddress(arg.Src);
                string dst = ResolveAddress(arg.Dst);
                Emit("MOVFF", src, dst);
            }
        }

        private void CompileFloatUnary(Unary arg)
        {
            if (IsFloatVal(arg.Dst))
            {
                for (int i = 0; i < 4; ++i)
                {
                    LoadExtendedByteIntoW(arg.Src, i);
                    if (i == 3) Emit("XORLW", "0x80");
                    StoreWIntoByte(arg.Dst, i);
                }

                return;
            }

            AllocFloatScratch();
            usesFloat = true;
            for (int i = 0; i < 4; ++i)
            {
                LoadExtendedByteIntoW(arg.Src, i);
                if (i == 3) Emit("XORLW", "0x80");
                Emit("MOVWF", Cell(FloatA, i), GetAccessMode(FloatA));
            }

            Emit("CALL", "__fp_fix");
            int dstWidth = WidthOf(arg.Dst);
            for (int i = 0; i < dstWidth && i < 4; ++i)
            {
                Emit("MOVF", Cell(FloatA, i), "W", GetAccessMode(FloatA));
                if (dstWidth == 1) StoreWInto(arg.Dst);
                else StoreWIntoByte(arg.Dst, i);
            }
        }

        private void CompileUnary(Unary arg)
        {
            if (arg.Op == PyMCU.IR.UnaryOp.Neg && (IsFloatVal(arg.Src) || IsFloatVal(arg.Dst)))
            {
                CompileFloatUnary(arg);
                return;
            }

            LoadIntoW(arg.Src);
            switch (arg.Op)
            {
                case PyMCU.IR.UnaryOp.Neg: Emit("NEGF", "WREG", "ACCESS"); break;
                case PyMCU.IR.UnaryOp.Not: Emit("COMF", "WREG", "W", "ACCESS"); break;
                case PyMCU.IR.UnaryOp.BitNot: Emit("COMF", "WREG", "W", "ACCESS"); break;
            }

            StoreWInto(arg.Dst);
        }

        private const string SignedCmpTmp = "__signed_cmp";

        private void EmitWideCompareSub(Val src1, Val src2, int width, bool signed)
        {
            string tmp = AllocAccessScratch(MultiByteTmp);
            string acc = AllocAccessScratch(MultiByteAcc);

            for (int i = 0; i < width; ++i)
            {
                bool top = i == width - 1;
                LoadExtendedByteIntoW(src1, i);
                if (signed && top) Emit("XORLW", "0x80");
                Emit("MOVWF", tmp, GetAccessMode(tmp));
                LoadExtendedByteIntoW(src2, i);
                if (signed && top) Emit("XORLW", "0x80");
                Emit(i == 0 ? "SUBWF" : "SUBWFB", tmp, "W", GetAccessMode(tmp));
                if (i == 0) Emit("MOVWF", acc, GetAccessMode(acc));
                else Emit("IORWF", acc, "F", GetAccessMode(acc));
            }

            Emit("MOVF", acc, "F", GetAccessMode(acc));
        }

        private int CompareWidth(Val src1, Val src2) =>
            Math.Max(WidthOf(src1), WidthOf(src2));

        private void EmitCompareSub(Val src1, Val src2, bool signed)
        {
            int width = CompareWidth(src1, src2);
            if (width > 1)
            {
                EmitWideCompareSub(src1, src2, width, signed);
                return;
            }

            if (!signed)
            {
                LoadIntoW(src2);
                string plain = ResolveAddress(src1);
                SelectBank(plain);
                Emit("SUBWF", plain, "W", GetAccessMode(plain));
                return;
            }

            AllocAccessScratch(SignedCmpTmp);

            EmitBiasedLoad(src1, true);
            Emit("MOVWF", SignedCmpTmp, GetAccessMode(SignedCmpTmp));
            LoadIntoW(src2);
            Emit("XORLW", "0x80");
            Emit("SUBWF", SignedCmpTmp, "W", GetAccessMode(SignedCmpTmp));
        }

        private void EmitBiasedLoad(Val val, bool signed)
        {
            string addr = ResolveAddress(val);
            SelectBank(addr);
            Emit("MOVF", addr, "W", GetAccessMode(addr));
            if (signed) Emit("XORLW", "0x80");
        }

        private static bool TryConstOperand(Val src2, bool signed, out int k)
        {
            k = 0;
            if (src2 is not Constant c) return false;
            if (signed)
            {
                if (c.Value < -128 || c.Value > 127) return false;
                k = (c.Value ^ 0x80) & 0xFF;
                return true;
            }

            if (c.Value < 0 || c.Value > 255) return false;
            k = c.Value;
            return true;
        }

        private void EmitShiftRightStep(bool signFill)
        {
            Emit("BCF", "STATUS", "C", "ACCESS");
            if (signFill)
            {
                Emit("BTFSC", "WREG", "7", "ACCESS");
                Emit("BSF", "STATUS", "C", "ACCESS");
            }

            Emit("RRCF", "WREG", "F", "ACCESS");
        }

        private void EmitVariableShift(Binary arg, bool left, bool arithmetic)
        {
            string lblLoop = MakeLabel("shift_loop");
            string lblEnd = MakeLabel("shift_end");
            string count = GetOrAllocVariable(MakeLabel("shift_count"));

            LoadIntoW(arg.Src2);
            SelectBank(count);
            Emit("MOVWF", count, GetAccessMode(count));
            LoadIntoW(arg.Src1);
            SelectBank(count);
            Emit("MOVF", count, "F", GetAccessMode(count));
            Emit("BZ", lblEnd);

            EmitLabel(lblLoop);
            if (left)
            {
                Emit("BCF", "STATUS", "C", "ACCESS");
                Emit("RLCF", "WREG", "F", "ACCESS");
            }
            else EmitShiftRightStep(arithmetic);

            SelectBank(count);
            Emit("DECFSZ", count, "F", GetAccessMode(count));
            Emit("BRA", lblLoop);
            EmitLabel(lblEnd);
        }

        private const string DivA = "__d_a";
        private const string DivB = "__d_b";
        private const string DivR = "__d_r";
        private const string DivB0 = "__d_b0";
        private const string DivCount = "__d_c";
        private const string DivNeg = "__d_neg";
        private const string DivSignA = "__d_sa";

        private readonly SortedSet<int> divWidths = new();

        private void AllocDivScratch()
        {
            AllocAccessScratch(DivA, 4);
            AllocAccessScratch(DivB, 4);
            AllocAccessScratch(DivR, 4);
            AllocAccessScratch(DivB0, 4);
            AllocAccessScratch(DivCount);
            AllocAccessScratch(DivNeg);
            AllocAccessScratch(DivSignA);
        }

        private static string Cell(string block, int i) => i == 0 ? block : $"{block}+{i}";

        private void EmitNegate(string block, int width)
        {
            for (int i = 0; i < width; ++i)
                Emit("COMF", Cell(block, i), "F", GetAccessMode(block));
            Emit("INCF", Cell(block, 0), "F", GetAccessMode(block));
            for (int i = 1; i < width; ++i)
            {
                Emit("BTFSC", "STATUS", "C", "ACCESS");
                Emit("INCF", Cell(block, i), "F", GetAccessMode(block));
            }
        }

        private void EmitLoadBlock(Val src, string block, int width)
        {
            for (int i = 0; i < width; ++i)
            {
                LoadExtendedByteIntoW(src, i);
                Emit("MOVWF", Cell(block, i), GetAccessMode(block));
            }
        }

        private void EmitStoreBlock(string block, Val dst, int width)
        {
            int dstWidth = WidthOf(dst);
            for (int i = 0; i < width && i < dstWidth; ++i)
            {
                Emit("MOVF", Cell(block, i), "W", GetAccessMode(block));
                if (dstWidth == 1) StoreWInto(dst);
                else StoreWIntoByte(dst, i);
            }
        }

        private void EmitDivisionRoutine(int width)
        {
            string name = $"__udiv{width * 8}";
            EmitComment($"--- {name}: {DivA} = {DivA} / {DivB}, {DivR} = remainder (restoring) ---");
            EmitLabel(name);
            for (int i = 0; i < width; ++i) Emit("CLRF", Cell(DivR, i), GetAccessMode(DivR));
            Emit("MOVLW", $"0x{width * 8:X2}");
            Emit("MOVWF", DivCount, GetAccessMode(DivCount));

            EmitLabel($"{name}_loop");
            Emit("BCF", "STATUS", "C", "ACCESS");
            for (int i = 0; i < width; ++i) Emit("RLCF", Cell(DivA, i), "F", GetAccessMode(DivA));
            for (int i = 0; i < width; ++i) Emit("RLCF", Cell(DivR, i), "F", GetAccessMode(DivR));
            Emit("BC", $"{name}_sub");

            for (int i = 0; i < width; ++i)
            {
                Emit("MOVF", Cell(DivB, i), "W", GetAccessMode(DivB));
                Emit(i == 0 ? "SUBWF" : "SUBWFB", Cell(DivR, i), "W", GetAccessMode(DivR));
            }

            Emit("BNC", $"{name}_next");

            EmitLabel($"{name}_sub");
            for (int i = 0; i < width; ++i)
            {
                Emit("MOVF", Cell(DivB, i), "W", GetAccessMode(DivB));
                Emit(i == 0 ? "SUBWF" : "SUBWFB", Cell(DivR, i), "F", GetAccessMode(DivR));
            }

            Emit("BSF", Cell(DivA, 0), "0", GetAccessMode(DivA));

            EmitLabel($"{name}_next");
            Emit("DECFSZ", DivCount, "F", GetAccessMode(DivCount));
            Emit("BRA", $"{name}_loop");
            Emit("RETURN");
        }

        private void EmitDivisionRoutines()
        {
            foreach (int width in divWidths) EmitDivisionRoutine(width);
        }

        private string MulOperandCell(Val val, int index)
        {
            if (val is not Constant && index < WidthOf(val)) return ByteOf(val, index);

            string stage = AllocAccessScratch(MulStage);
            LoadExtendedByteIntoW(val, index);
            Emit("MOVWF", stage, GetAccessMode(stage));
            return stage;
        }

        private void EmitPartialProduct(Val a, int ai, Val b, int bi)
        {
            string cell = MulOperandCell(b, bi);
            LoadExtendedByteIntoW(a, ai);
            SelectBank(cell);
            Emit("MULWF", cell, GetAccessMode(cell));
        }

        private bool IsStaticZeroByte(Val val, int index)
        {
            if (val is Constant c) return ((c.Value >> (8 * index)) & 0xFF) == 0;
            if (val is FloatConstant) return false;
            return index >= WidthOf(val) && !GetValType(val).IsSigned();
        }

        private void CompileWideMul32(Binary arg)
        {
            string result = AllocAccessScratch(MulResult, 4);
            for (int i = 0; i < 4; ++i)
                Emit("CLRF", Cell(result, i), GetAccessMode(result));

            for (int i = 0; i < 4; ++i)
            for (int j = 0; i + j < 4; ++j)
            {
                if (IsStaticZeroByte(arg.Src1, i) || IsStaticZeroByte(arg.Src2, j)) continue;

                EmitPartialProduct(arg.Src1, i, arg.Src2, j);
                int k = i + j;
                Emit("MOVF", "PRODL", "W", "ACCESS");
                Emit("ADDWF", Cell(result, k), "F", GetAccessMode(result));
                if (k + 1 >= 4) continue;

                Emit("MOVF", "PRODH", "W", "ACCESS");
                Emit("ADDWFC", Cell(result, k + 1), "F", GetAccessMode(result));
                if (k + 2 >= 4) continue;

                Emit("MOVLW", "0x00");
                for (int m = k + 2; m < 4; ++m)
                    Emit("ADDWFC", Cell(result, m), "F", GetAccessMode(result));
            }

            for (int i = 0; i < 4; ++i)
            {
                Emit("MOVF", Cell(result, i), "W", GetAccessMode(result));
                StoreWIntoByte(arg.Dst, i);
            }
        }

        private void CompileWideMul(Binary arg)
        {
            string result = AllocAccessScratch(MulResult, 2);

            EmitPartialProduct(arg.Src1, 0, arg.Src2, 0);
            Emit("MOVFF", "PRODL", Cell(result, 0));
            Emit("MOVFF", "PRODH", Cell(result, 1));

            EmitPartialProduct(arg.Src1, 0, arg.Src2, 1);
            Emit("MOVF", "PRODL", "W", "ACCESS");
            Emit("ADDWF", Cell(result, 1), "F", GetAccessMode(result));

            EmitPartialProduct(arg.Src1, 1, arg.Src2, 0);
            Emit("MOVF", "PRODL", "W", "ACCESS");
            Emit("ADDWF", Cell(result, 1), "F", GetAccessMode(result));

            Emit("MOVF", Cell(result, 0), "W", GetAccessMode(result));
            StoreWIntoByte(arg.Dst, 0);
            Emit("MOVF", Cell(result, 1), "W", GetAccessMode(result));
            StoreWIntoByte(arg.Dst, 1);
        }

        private static bool IsRelational(PyMCU.IR.BinaryOp op) =>
            op is PyMCU.IR.BinaryOp.Equal or PyMCU.IR.BinaryOp.NotEqual
                or PyMCU.IR.BinaryOp.LessThan or PyMCU.IR.BinaryOp.LessEqual
                or PyMCU.IR.BinaryOp.GreaterThan or PyMCU.IR.BinaryOp.GreaterEqual;

        private void AllocFloatScratch()
        {
            usesFloat = true;
            AllocAccessScratch(FloatA, 4);
            AllocAccessScratch(FloatB, 4);
            AllocAccessScratch(FloatScratch, 24);
        }

        private void EmitFloatOperands(Val a, Val b)
        {
            AllocFloatScratch();
            for (int i = 0; i < 4; ++i)
            {
                LoadExtendedByteIntoW(a, i);
                Emit("MOVWF", Cell(FloatA, i), GetAccessMode(FloatA));
            }

            for (int i = 0; i < 4; ++i)
            {
                LoadExtendedByteIntoW(b, i);
                Emit("MOVWF", Cell(FloatB, i), GetAccessMode(FloatB));
            }
        }

        // __fp_cmp answers in W: 0x00 equal, 0x01 greater (or unordered), 0xFF less.
        // Each relation reduces to "is W the code I want", so one XOR/AND plus a
        // Z-branch decides it.
        private void EmitFloatCompareBranch(PyMCU.IR.BinaryOp op, string target)
        {
            bool zeroMeansTrue = op is PyMCU.IR.BinaryOp.Equal or PyMCU.IR.BinaryOp.LessThan
                or PyMCU.IR.BinaryOp.GreaterThan;

            switch (op)
            {
                case PyMCU.IR.BinaryOp.Equal:
                case PyMCU.IR.BinaryOp.NotEqual: Emit("ANDLW", "0xFF"); break;
                case PyMCU.IR.BinaryOp.LessThan:
                case PyMCU.IR.BinaryOp.GreaterEqual: Emit("XORLW", "0xFF"); break;
                default: Emit("XORLW", "0x01"); break;
            }

            Emit(zeroMeansTrue ? "BZ" : "BNZ", target);
        }

        private void CompileFloatCompareJump(Val src1, Val src2, PyMCU.IR.BinaryOp op, string target)
        {
            EmitFloatOperands(src1, src2);
            Emit("CALL", "__fp_cmp");
            EmitFloatCompareBranch(op, target);
        }

        private void CompileFloatArith(Binary arg, string routine)
        {
            EmitFloatOperands(arg.Src1, arg.Src2);
            Emit("CALL", routine);
            if (arg.Op == PyMCU.IR.BinaryOp.FloorDiv) Emit("CALL", "__fp_floor");

            if (!IsFloatVal(arg.Dst))
            {
                Emit("CALL", "__fp_fix");
                int narrow = WidthOf(arg.Dst);
                for (int i = 0; i < narrow && i < 4; ++i)
                {
                    Emit("MOVF", Cell(FloatA, i), "W", GetAccessMode(FloatA));
                    if (narrow == 1) StoreWInto(arg.Dst);
                    else StoreWIntoByte(arg.Dst, i);
                }

                return;
            }

            for (int i = 0; i < 4; ++i)
            {
                Emit("MOVF", Cell(FloatA, i), "W", GetAccessMode(FloatA));
                StoreWIntoByte(arg.Dst, i);
            }
        }

        private void CompileFloatCompareValue(Binary arg)
        {
            EmitFloatOperands(arg.Src1, arg.Src2);
            Emit("CALL", "__fp_cmp");
            string lblTrue = MakeLabel("fcmp_true");
            string lblEnd = MakeLabel("fcmp_end");
            EmitFloatCompareBranch(arg.Op, lblTrue);
            Emit("MOVLW", "0x00");
            Emit("BRA", lblEnd);
            EmitLabel(lblTrue);
            Emit("MOVLW", "0x01");
            EmitLabel(lblEnd);
            StoreWInto(arg.Dst);
        }

        private void CompileDivision(Binary arg)
        {
            int width = Math.Max(WidthOf(arg.Dst), CompareWidth(arg.Src1, arg.Src2));
            if (width > 4)
                throw new NotSupportedException(
                    "PIC18CodeGen: division on operands wider than 32 bits is not implemented yet");

            bool signed = IsSignedComparison(arg.Src1, arg.Src2);
            bool wantRemainder = arg.Op == PyMCU.IR.BinaryOp.Mod;

            AllocDivScratch();
            divWidths.Add(width);

            EmitLoadBlock(arg.Src1, DivA, width);
            EmitLoadBlock(arg.Src2, DivB, width);

            if (signed)
            {
                for (int i = 0; i < width; ++i)
                {
                    Emit("MOVF", Cell(DivB, i), "W", GetAccessMode(DivB));
                    Emit("MOVWF", Cell(DivB0, i), GetAccessMode(DivB0));
                }

                string lblPosA = MakeLabel("div_pos_a");
                string lblPosB = MakeLabel("div_pos_b");

                Emit("CLRF", DivSignA, GetAccessMode(DivSignA));
                Emit("CLRF", DivNeg, GetAccessMode(DivNeg));

                Emit("BTFSS", Cell(DivA, width - 1), "7", GetAccessMode(DivA));
                Emit("BRA", lblPosA);
                Emit("SETF", DivSignA, GetAccessMode(DivSignA));
                Emit("SETF", DivNeg, GetAccessMode(DivNeg));
                EmitNegate(DivA, width);
                EmitLabel(lblPosA);

                Emit("BTFSS", Cell(DivB, width - 1), "7", GetAccessMode(DivB));
                Emit("BRA", lblPosB);
                Emit("COMF", DivNeg, "F", GetAccessMode(DivNeg));
                EmitNegate(DivB, width);
                EmitLabel(lblPosB);

                Emit("CALL", $"__udiv{width * 8}");

                string lblQ = MakeLabel("div_q_done");
                string lblR = MakeLabel("div_r_done");
                string lblNoCorr = MakeLabel("div_no_corr");

                Emit("BTFSS", DivNeg, "0", GetAccessMode(DivNeg));
                Emit("BRA", lblQ);
                EmitNegate(DivA, width);
                EmitLabel(lblQ);

                Emit("BTFSS", DivSignA, "0", GetAccessMode(DivSignA));
                Emit("BRA", lblR);
                EmitNegate(DivR, width);
                EmitLabel(lblR);

                Emit("BTFSS", DivNeg, "0", GetAccessMode(DivNeg));
                Emit("BRA", lblNoCorr);
                Emit("MOVF", Cell(DivR, 0), "W", GetAccessMode(DivR));
                for (int i = 1; i < width; ++i)
                    Emit("IORWF", Cell(DivR, i), "W", GetAccessMode(DivR));
                Emit("BZ", lblNoCorr);

                string lblDec = MakeLabel("div_dec_done");
                Emit("DECF", Cell(DivA, 0), "F", GetAccessMode(DivA));
                for (int i = 1; i < width; ++i)
                {
                    Emit("BC", lblDec);
                    Emit("DECF", Cell(DivA, i), "F", GetAccessMode(DivA));
                }

                EmitLabel(lblDec);
                for (int i = 0; i < width; ++i)
                {
                    Emit("MOVF", Cell(DivB0, i), "W", GetAccessMode(DivB0));
                    Emit(i == 0 ? "ADDWF" : "ADDWFC", Cell(DivR, i), "F", GetAccessMode(DivR));
                }

                EmitLabel(lblNoCorr);
            }
            else
            {
                Emit("CALL", $"__udiv{width * 8}");
            }

            EmitStoreBlock(wantRemainder ? DivR : DivA, arg.Dst, width);
        }

        private void CompileBinary(Binary arg)
        {
            if (IsRelational(arg.Op) && (IsFloatVal(arg.Src1) || IsFloatVal(arg.Src2)))
            {
                CompileFloatCompareValue(arg);
                return;
            }

            if ((IsFloatVal(arg.Dst) || IsFloatVal(arg.Src1) || IsFloatVal(arg.Src2))
                && arg.Op is PyMCU.IR.BinaryOp.Add or PyMCU.IR.BinaryOp.Sub
                    or PyMCU.IR.BinaryOp.Mul or PyMCU.IR.BinaryOp.Div
                    or PyMCU.IR.BinaryOp.FloorDiv)
            {
                CompileFloatArith(arg, arg.Op switch
                {
                    PyMCU.IR.BinaryOp.Add => "__fp_add",
                    PyMCU.IR.BinaryOp.Sub => "__fp_sub",
                    PyMCU.IR.BinaryOp.Mul => "__fp_mul",
                    _ => "__fp_div",

                });
                return;
            }

            if (IsWideArithmetic(arg.Op) && WidthOf(arg.Dst) > 1)
            {
                CompileWideArithmetic(arg, WidthOf(arg.Dst));
                return;
            }

            if (arg.Op is PyMCU.IR.BinaryOp.LShift or PyMCU.IR.BinaryOp.RShift
                && WidthOf(arg.Dst) > 1)
            {
                CompileWideShift(arg, WidthOf(arg.Dst),
                    arg.Op == PyMCU.IR.BinaryOp.LShift, IsArithmeticShift(arg.Op, arg.Src1));
                return;
            }

            if (arg.Op is PyMCU.IR.BinaryOp.Mul or PyMCU.IR.BinaryOp.Div
                or PyMCU.IR.BinaryOp.FloorDiv or PyMCU.IR.BinaryOp.Mod)
            {
                int wide = Math.Max(WidthOf(arg.Dst), CompareWidth(arg.Src1, arg.Src2));
                bool narrowOperands = WidthOf(arg.Src1) == 1 && WidthOf(arg.Src2) == 1;

                if (arg.Op == PyMCU.IR.BinaryOp.Mul && wide == 2 && narrowOperands)
                {
                    LoadIntoW(arg.Src1);
                    string factor = ResolveAddress(arg.Src2);
                    if (arg.Src2 is Constant c2w)
                    {
                        string mulTmp = AllocAccessScratch(MultiByteTmp);
                        Emit("MOVLW", $"0x{c2w.Value & 0xFF:X2}");
                        Emit("MOVWF", mulTmp, GetAccessMode(mulTmp));
                        LoadIntoW(arg.Src1);
                        factor = mulTmp;
                    }

                    SelectBank(factor);
                    Emit("MULWF", factor, GetAccessMode(factor));
                    Emit("MOVF", "PRODL", "W", "ACCESS");
                    StoreWIntoByte(arg.Dst, 0);
                    Emit("MOVF", "PRODH", "W", "ACCESS");
                    StoreWIntoByte(arg.Dst, 1);
                    return;
                }

                if (arg.Op == PyMCU.IR.BinaryOp.Mul && wide == 2)
                {
                    CompileWideMul(arg);
                    return;
                }

                if (arg.Op == PyMCU.IR.BinaryOp.Mul && wide == 4)
                {
                    CompileWideMul32(arg);
                    return;
                }

                if (arg.Op == PyMCU.IR.BinaryOp.Mul && wide > 2)
                    throw new NotSupportedException(
                        $"PIC18CodeGen: Mul into a {wide}-byte destination is not implemented " +
                        "yet for the PIC18 backend. The result would otherwise be silently " +
                        "truncated.");
            }

            if (arg.Op is PyMCU.IR.BinaryOp.Div or PyMCU.IR.BinaryOp.FloorDiv or PyMCU.IR.BinaryOp.Mod)
            {
                CompileDivision(arg);
                return;
            }

            if (arg.Op == PyMCU.IR.BinaryOp.Mul)
            {
                if (arg.Src2 is Constant c2m)
                {
                    LoadIntoW(arg.Src1);
                    Emit("MOVLW", $"0x{c2m.Value & 0xFF:X2}");
                    Emit("MULWF", "WREG", "ACCESS");
                    Emit("MOVF", "PRODL", "W", "ACCESS");
                    StoreWInto(arg.Dst);
                    return;
                }
                else
                {
                    LoadIntoW(arg.Src1);
                    string right = ResolveAddress(arg.Src2);
                    SelectBank(right);
                    Emit("MULWF", right, GetAccessMode(right));
                    Emit("MOVF", "PRODL", "W", "ACCESS");
                    StoreWInto(arg.Dst);
                    return;
                }
            }

            LoadIntoW(arg.Src1);
            string r = ResolveAddress(arg.Src2);

            switch (arg.Op)
            {
                case PyMCU.IR.BinaryOp.Add:
                    if (arg.Src2 is Constant) Emit("ADDLW", r);
                    else
                    {
                        SelectBank(r);
                        Emit("ADDWF", r, "W", GetAccessMode(r));
                    }

                    StoreWInto(arg.Dst);
                    break;

                case PyMCU.IR.BinaryOp.Sub:
                    if (arg.Src2 is Constant c2s)
                        Emit("ADDLW", $"(0x100 - ({r})) & 0xFF");
                    else if (arg.Src1 is Constant c1s)
                    {
                        LoadIntoW(arg.Src2);
                        Emit("SUBLW", $"0x{c1s.Value & 0xFF:X2}");
                    }
                    else
                    {
                        LoadIntoW(arg.Src2);
                        string left = ResolveAddress(arg.Src1);
                        SelectBank(left);
                        Emit("SUBWF", left, "W", GetAccessMode(left));
                    }

                    StoreWInto(arg.Dst);
                    break;

                case PyMCU.IR.BinaryOp.BitAnd:
                    if (arg.Src2 is Constant) Emit("ANDLW", r);
                    else
                    {
                        SelectBank(r);
                        Emit("ANDWF", r, "W", GetAccessMode(r));
                    }

                    StoreWInto(arg.Dst);
                    break;

                case PyMCU.IR.BinaryOp.BitOr:
                    if (arg.Src2 is Constant) Emit("IORLW", r);
                    else
                    {
                        SelectBank(r);
                        Emit("IORWF", r, "W", GetAccessMode(r));
                    }

                    StoreWInto(arg.Dst);
                    break;

                case PyMCU.IR.BinaryOp.BitXor:
                    if (arg.Src2 is Constant) Emit("XORLW", r);
                    else
                    {
                        SelectBank(r);
                        Emit("XORWF", r, "W", GetAccessMode(r));
                    }

                    StoreWInto(arg.Dst);
                    break;

                case PyMCU.IR.BinaryOp.LShift:
                    if (arg.Src2 is Constant c2ls)
                    {
                        int amount = c2ls.Value;
                        if (amount >= 8) Emit("CLRF", "WREG", "ACCESS");
                        else
                            for (int i = 0; i < amount; ++i)
                            {
                                Emit("BCF", "STATUS", "C", "ACCESS");
                                Emit("RLCF", "WREG", "F", "ACCESS");
                            }
                    }
                    else EmitVariableShift(arg, left: true, arithmetic: false);

                    StoreWInto(arg.Dst);
                    break;

                case PyMCU.IR.BinaryOp.RShift:
                {
                    bool arithmetic = GetValType(arg.Src1).IsSigned()
                                      || (arg.Src1 is Constant cneg && cneg.Value < 0);
                    if (arg.Src2 is Constant c2rs)
                    {
                        int width = WidthOf(arg.Src1);
                        int amount = Math.Max(c2rs.Value, 0);
                        if (arithmetic) amount = Math.Min(amount, 8 * width - 1);
                        int byteOffset = amount / 8;
                        int bitShift = amount % 8;

                        if (byteOffset >= width) Emit("CLRF", "WREG", "ACCESS");
                        else
                        {
                            if (byteOffset > 0) LoadByteIntoW(arg.Src1, byteOffset);
                            bool signFill = arithmetic && byteOffset == width - 1;
                            for (int i = 0; i < bitShift; ++i) EmitShiftRightStep(signFill);
                        }
                    }
                    else EmitVariableShift(arg, left: false, arithmetic: arithmetic);

                    StoreWInto(arg.Dst);
                    break;
                }


                case PyMCU.IR.BinaryOp.Equal:
                case PyMCU.IR.BinaryOp.NotEqual:
                case PyMCU.IR.BinaryOp.LessThan:
                case PyMCU.IR.BinaryOp.LessEqual:
                case PyMCU.IR.BinaryOp.GreaterThan:
                case PyMCU.IR.BinaryOp.GreaterEqual:
                {
                    bool isEquality = arg.Op is PyMCU.IR.BinaryOp.Equal or PyMCU.IR.BinaryOp.NotEqual;
                    bool optimizeZero = isEquality && CompareWidth(arg.Src1, arg.Src2) == 1
                                        && arg.Src2 is Constant cz && cz.Value == 0;

                    if (optimizeZero)
                    {
                        string left = ResolveAddress(arg.Src1);
                        SelectBank(left);
                        Emit("MOVF", left, "F", GetAccessMode(left));
                    }
                    else
                    {
                        EmitCompareSub(arg.Src1, arg.Src2,
                            !isEquality && IsSignedComparison(arg.Src1, arg.Src2));
                    }

                    Emit("MOVLW", "0x00");
                    string lblFalse = MakeLabel("comp_false");
                    string lblTrue = MakeLabel("comp_true");

                    switch (arg.Op)
                    {
                        case PyMCU.IR.BinaryOp.Equal:
                            Emit("BTFSC", "STATUS", "Z", "ACCESS");
                            Emit("MOVLW", "0x01");
                            break;
                        case PyMCU.IR.BinaryOp.NotEqual:
                            Emit("BTFSS", "STATUS", "Z", "ACCESS");
                            Emit("MOVLW", "0x01");
                            break;
                        case PyMCU.IR.BinaryOp.LessThan:
                            Emit("BTFSS", "STATUS", "C", "ACCESS");
                            Emit("MOVLW", "0x01");
                            break;
                        case PyMCU.IR.BinaryOp.GreaterEqual:
                            Emit("BTFSC", "STATUS", "C", "ACCESS");
                            Emit("MOVLW", "0x01");
                            break;
                        case PyMCU.IR.BinaryOp.GreaterThan:
                            Emit("BTFSS", "STATUS", "C", "ACCESS");
                            Emit("BRA", lblFalse);
                            Emit("BTFSS", "STATUS", "Z", "ACCESS");
                            Emit("MOVLW", "0x01");
                            break;
                        case PyMCU.IR.BinaryOp.LessEqual:
                            Emit("BTFSC", "STATUS", "Z", "ACCESS");
                            Emit("BRA", lblTrue);
                            Emit("BTFSC", "STATUS", "C", "ACCESS");
                            Emit("BRA", lblFalse);
                            EmitLabel(lblTrue);
                            Emit("MOVLW", "0x01");
                            break;
                    }

                    EmitLabel(lblFalse);
                    StoreWInto(arg.Dst);
                    break;
                }
            }
        }

        private void CompileJumpIfZero(JumpIfZero arg)
        {
            LoadIntoW(arg.Condition);
            Emit("ANDLW", "0xFF");
            Emit("BZ", arg.Target);
        }

        private void CompileJumpIfNotZero(JumpIfNotZero arg)
        {
            LoadIntoW(arg.Condition);
            Emit("ANDLW", "0xFF");
            Emit("BNZ", arg.Target);
        }

        private void CompileSignalError(SignalError arg)
        {
            string err = AllocAccessScratch(ErrCell);
            LoadIntoW(arg.Code);
            Emit("MOVWF", err, GetAccessMode(err));

            if (arg.CatchLabel != null)
            {
                Emit("BRA", arg.CatchLabel);
                return;
            }

            if (currentFunction?.Name == "main")
            {
                needsErrTrap = true;
                Emit("GOTO", ErrTrapLabel);
                return;
            }

            Emit("RETURN");
        }

        private void CompileSignalSuccess()
        {
            string err = AllocAccessScratch(ErrCell);
            Emit("CLRF", err, GetAccessMode(err));
        }

        private void CompileBranchOnError(BranchOnError arg)
        {
            string err = AllocAccessScratch(ErrCell);
            Emit("MOVF", err, "F", GetAccessMode(err));
            string target = arg.ErrorLabel;
            if (target == UnhandledExnLabel)
            {
                needsErrTrap = true;
                target = ErrTrapLabel;
            }

            Emit("BNZ", target);
        }

        private void EmitErrorTrap()
        {
            if (!needsErrTrap) return;
            EmitComment("--- unhandled error: park here so the fault is observable ---");
            EmitLabel(ErrTrapLabel);
            Emit("GOTO", ErrTrapLabel);
        }

        private void CompileCall(Call arg)
        {
            if (arg.Args.Count > 0)
            {
                if (!functionParams.TryGetValue(arg.FunctionName, out var names) || names.Count < arg.Args.Count)
                    throw new NotSupportedException(
                        $"PIC18 backend: call to '{arg.FunctionName}' passes {arg.Args.Count} arguments " +
                        "but the callee declares fewer parameters.");

                for (int i = 0; i < arg.Args.Count; i++)
                {
                    if (!stackLayout.ContainsKey(names[i])) GetOrAllocVariable(names[i]);
                    CompileCopy(new Copy(arg.Args[i], new Variable(names[i], GetValType(arg.Args[i]))));
                }
            }

            Emit("CALL", arg.FunctionName);
            if (arg.Dst is NoneVal) return;

            int retWidth = WidthOf(arg.Dst);
            if (retWidth <= 1)
            {
                StoreWInto(arg.Dst);
                return;
            }

            AllocAccessScratch(ReturnHigh, 3);
            StoreWIntoByte(arg.Dst, 0);
            for (int i = 1; i < retWidth && i <= 3; ++i)
            {
                Emit("MOVF", Cell(ReturnHigh, i - 1), "W", GetAccessMode(ReturnHigh));
                StoreWIntoByte(arg.Dst, i);
            }
        }

        private void CompileBitSet(BitSet arg)
        {
            string addr = ResolveAddress(arg.Target);
            SelectBank(addr);
            Emit("BSF", addr, $"{arg.Bit}", GetAccessMode(addr));
        }

        private void CompileBitClear(BitClear arg)
        {
            string addr = ResolveAddress(arg.Target);
            SelectBank(addr);
            Emit("BCF", addr, $"{arg.Bit}", GetAccessMode(addr));
        }

        private void CompileBitCheck(BitCheck arg)
        {
            string addr = ResolveAddress(arg.Source);
            SelectBank(addr);
            Emit("CLRF", "WREG", "ACCESS");
            Emit("BTFSC", addr, $"{arg.Bit}", GetAccessMode(addr));
            Emit("MOVLW", "0x01");
            StoreWInto(arg.Dst);
        }

        private void CompileBitWrite(BitWrite arg)
        {
            string addr = ResolveAddress(arg.Target);
            SelectBank(addr);
            LoadIntoW(arg.Src);
            string lblSet = MakeLabel("set_bit");
            string lblEnd = MakeLabel("end_bit_write");
            Emit("TSTFSZ", "WREG", "ACCESS");
            Emit("BRA", lblSet);
            Emit("BCF", addr, $"{arg.Bit}", GetAccessMode(addr));
            Emit("BRA", lblEnd);
            EmitLabel(lblSet);
            Emit("BSF", addr, $"{arg.Bit}", GetAccessMode(addr));
            EmitLabel(lblEnd);
        }

        private void CompileJumpIfBitSet(JumpIfBitSet arg)
        {
            string addr = ResolveAddress(arg.Source);
            SelectBank(addr);
            Emit("BTFSC", addr, $"{arg.Bit}", GetAccessMode(addr));
            Emit("BRA", arg.Target);
        }

        private void CompileJumpIfBitClear(JumpIfBitClear arg)
        {
            string addr = ResolveAddress(arg.Source);
            SelectBank(addr);
            Emit("BTFSS", addr, $"{arg.Bit}", GetAccessMode(addr));
            Emit("BRA", arg.Target);
        }

        private void CompileJumpIfEqual(JumpIfEqual arg)
        {
            if (IsFloatVal(arg.Src1) || IsFloatVal(arg.Src2))
            {
                CompileFloatCompareJump(arg.Src1, arg.Src2, PyMCU.IR.BinaryOp.Equal, arg.Target);
                return;
            }

            if (CompareWidth(arg.Src1, arg.Src2) == 1
                && arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
            {
                string src1 = ResolveAddress(arg.Src1);
                SelectBank(src1);
                Emit("MOVF", src1, "W", GetAccessMode(src1));
                Emit("XORLW", $"0x{c2.Value:X2}");
                Emit("BTFSC", "STATUS", "Z", "ACCESS");
                Emit("BRA", arg.Target);
                return;
            }

            EmitCompareSub(arg.Src1, arg.Src2, false);
            Emit("BTFSC", "STATUS", "Z", "ACCESS");
            Emit("BRA", arg.Target);
        }

        private void CompileJumpIfNotEqual(JumpIfNotEqual arg)
        {
            if (IsFloatVal(arg.Src1) || IsFloatVal(arg.Src2))
            {
                CompileFloatCompareJump(arg.Src1, arg.Src2, PyMCU.IR.BinaryOp.NotEqual, arg.Target);
                return;
            }

            if (CompareWidth(arg.Src1, arg.Src2) == 1
                && arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
            {
                string src1 = ResolveAddress(arg.Src1);
                SelectBank(src1);
                Emit("MOVF", src1, "W", GetAccessMode(src1));
                Emit("XORLW", $"0x{c2.Value:X2}");
                Emit("BTFSS", "STATUS", "Z", "ACCESS");
                Emit("BRA", arg.Target);
                return;
            }

            EmitCompareSub(arg.Src1, arg.Src2, false);
            Emit("BTFSS", "STATUS", "Z", "ACCESS");
            Emit("BRA", arg.Target);
        }

        private void CompileJumpIfLessThan(JumpIfLessThan arg)
        {
            if (IsFloatVal(arg.Src1) || IsFloatVal(arg.Src2))
            {
                CompileFloatCompareJump(arg.Src1, arg.Src2, PyMCU.IR.BinaryOp.LessThan, arg.Target);
                return;
            }

            bool signed = IsSignedComparison(arg.Src1, arg.Src2);
            if (CompareWidth(arg.Src1, arg.Src2) == 1 && TryConstOperand(arg.Src2, signed, out int k))
            {
                if (k == 0) return;
                EmitBiasedLoad(arg.Src1, signed);
                Emit("SUBLW", $"0x{k - 1:X2}");
                Emit("BTFSC", "STATUS", "C", "ACCESS");
                Emit("BRA", arg.Target);
                return;
            }

            EmitCompareSub(arg.Src1, arg.Src2, signed);
            Emit("BTFSS", "STATUS", "C", "ACCESS");
            Emit("BRA", arg.Target);
        }

        private void CompileJumpIfLessOrEqual(JumpIfLessOrEqual arg)
        {
            if (IsFloatVal(arg.Src1) || IsFloatVal(arg.Src2))
            {
                CompileFloatCompareJump(arg.Src1, arg.Src2, PyMCU.IR.BinaryOp.LessEqual, arg.Target);
                return;
            }

            bool signed = IsSignedComparison(arg.Src1, arg.Src2);
            if (CompareWidth(arg.Src1, arg.Src2) == 1 && TryConstOperand(arg.Src2, signed, out int k))
            {
                EmitBiasedLoad(arg.Src1, signed);
                Emit("SUBLW", $"0x{k:X2}");
                Emit("BTFSC", "STATUS", "C", "ACCESS");
                Emit("BRA", arg.Target);
                return;
            }

            EmitCompareSub(arg.Src2, arg.Src1, signed);
            Emit("BTFSC", "STATUS", "C", "ACCESS");
            Emit("BRA", arg.Target);
        }

        private void CompileJumpIfGreaterThan(JumpIfGreaterThan arg)
        {
            if (IsFloatVal(arg.Src1) || IsFloatVal(arg.Src2))
            {
                CompileFloatCompareJump(arg.Src1, arg.Src2, PyMCU.IR.BinaryOp.GreaterThan, arg.Target);
                return;
            }

            bool signed = IsSignedComparison(arg.Src1, arg.Src2);
            if (CompareWidth(arg.Src1, arg.Src2) == 1 && TryConstOperand(arg.Src2, signed, out int k))
            {
                EmitBiasedLoad(arg.Src1, signed);
                Emit("SUBLW", $"0x{k:X2}");
                Emit("BTFSS", "STATUS", "C", "ACCESS");
                Emit("BRA", arg.Target);
                return;
            }

            EmitCompareSub(arg.Src2, arg.Src1, signed);
            Emit("BTFSS", "STATUS", "C", "ACCESS");
            Emit("BRA", arg.Target);
        }

        private void CompileJumpIfGreaterOrEqual(JumpIfGreaterOrEqual arg)
        {
            if (IsFloatVal(arg.Src1) || IsFloatVal(arg.Src2))
            {
                CompileFloatCompareJump(arg.Src1, arg.Src2, PyMCU.IR.BinaryOp.GreaterEqual, arg.Target);
                return;
            }

            bool signed = IsSignedComparison(arg.Src1, arg.Src2);
            if (CompareWidth(arg.Src1, arg.Src2) == 1 && TryConstOperand(arg.Src2, signed, out int k))
            {
                if (k == 0)
                {
                    Emit("BRA", arg.Target);
                    return;
                }

                EmitBiasedLoad(arg.Src1, signed);
                Emit("SUBLW", $"0x{k - 1:X2}");
                Emit("BTFSS", "STATUS", "C", "ACCESS");
                Emit("BRA", arg.Target);
                return;
            }

            EmitCompareSub(arg.Src1, arg.Src2, signed);
            Emit("BTFSC", "STATUS", "C", "ACCESS");
            Emit("BRA", arg.Target);
        }

        private void CompileAugAssign(AugAssign aa)
        {
            int wideWidth = WidthOf(aa.Target);
            if (wideWidth > 1)
            {
                var asBinary = new Binary(aa.Op, aa.Target, aa.Operand, aa.Target);
                if (IsWideArithmetic(aa.Op))
                {
                    CompileWideArithmetic(asBinary, wideWidth);
                    return;
                }

                if (aa.Op is PyMCU.IR.BinaryOp.LShift or PyMCU.IR.BinaryOp.RShift)
                {
                    CompileWideShift(asBinary, wideWidth, aa.Op == PyMCU.IR.BinaryOp.LShift,
                        IsArithmeticShift(aa.Op, aa.Target));
                    return;
                }
            }

            if (aa.Op is not (PyMCU.IR.BinaryOp.Add or PyMCU.IR.BinaryOp.Sub
                or PyMCU.IR.BinaryOp.BitAnd or PyMCU.IR.BinaryOp.BitOr or PyMCU.IR.BinaryOp.BitXor))
            {
                CompileBinary(new Binary(aa.Op, aa.Target, aa.Operand, aa.Target));
                return;
            }

            string addr = ResolveAddress(aa.Target);
            LoadIntoW(aa.Operand);
            SelectBank(addr);
            string access = GetAccessMode(addr);
            switch (aa.Op)
            {
                case PyMCU.IR.BinaryOp.Add:    Emit("ADDWF", addr, "F", access); break;
                case PyMCU.IR.BinaryOp.Sub:    Emit("SUBWF", addr, "F", access); break;
                case PyMCU.IR.BinaryOp.BitAnd: Emit("ANDWF", addr, "F", access); break;
                case PyMCU.IR.BinaryOp.BitOr:  Emit("IORWF", addr, "F", access); break;
                case PyMCU.IR.BinaryOp.BitXor: Emit("XORWF", addr, "F", access); break;
            }
        }

        private string ArrayBaseOperand(string arrayName)
        {
            if (stackLayout.ContainsKey(arrayName) || symbolTable.ContainsKey(arrayName))
                return arrayName;
            throw new InvalidOperationException(
                $"PIC18CodeGen: array '{arrayName}' has no allocated storage");
        }

        private static string OffsetOperand(string baseOp, int offset)
        {
            if (offset == 0) return baseOp;
            return baseOp.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? $"0x{Convert.ToInt32(baseOp, 16) + offset:X3}"
                : $"{baseOp}+{offset}";
        }

        private static int ArrayElemSize(DataType elemType)
        {
            int size = elemType.SizeOf();
            if (size > 2)
                throw new NotSupportedException(
                    "PIC18CodeGen: array elements wider than 16 bits are not implemented yet");
            return size;
        }

        private static void CheckConstIndex(string arrayName, int index, int count)
        {
            if (index < 0 || index >= count)
                throw new InvalidOperationException(
                    $"PIC18CodeGen: constant index {index} is out of bounds for array " +
                    $"'{arrayName}' of length {count}");
        }

        private void EmitArrayElementPointer(string arrayName, Val index, int elemSize)
        {
            Emit("LFSR", "0", ArrayBaseOperand(arrayName));
            for (int i = 0; i < elemSize; ++i)
            {
                LoadIntoW(index);
                Emit("ADDWF", "FSR0L", "F", "ACCESS");
                Emit("MOVLW", "0x00");
                Emit("ADDWFC", "FSR0H", "F", "ACCESS");
            }
        }

        private void CompileArrayLoad(ArrayLoad arg)
        {
            int elemSize = ArrayElemSize(arg.ElemType);

            if (arg.Index is Constant ci)
            {
                CheckConstIndex(arg.ArrayName, ci.Value, arg.Count);
                string baseOp = ArrayBaseOperand(arg.ArrayName);
                for (int i = 0; i < elemSize; ++i)
                {
                    string cell = OffsetOperand(baseOp, ci.Value * elemSize + i);
                    SelectBank(cell);
                    Emit("MOVF", cell, "W", GetAccessMode(cell));
                    if (elemSize == 1) StoreWInto(arg.Dst);
                    else StoreWIntoByte(arg.Dst, i);
                }

                return;
            }

            EmitArrayElementPointer(arg.ArrayName, arg.Index, elemSize);
            if (elemSize == 1)
            {
                Emit("MOVF", "INDF0", "W", "ACCESS");
                StoreWInto(arg.Dst);
                return;
            }

            Emit("MOVF", "POSTINC0", "W", "ACCESS");
            StoreWIntoByte(arg.Dst, 0);
            Emit("MOVF", "INDF0", "W", "ACCESS");
            StoreWIntoByte(arg.Dst, 1);
        }

        private void CompileArrayStore(ArrayStore arg)
        {
            int elemSize = ArrayElemSize(arg.ElemType);
            if (arg.Src is FunctionRef)
                throw new NotSupportedException("PIC18CodeGen: Callable arrays are not implemented yet");

            if (arg.Index is Constant ci)
            {
                CheckConstIndex(arg.ArrayName, ci.Value, arg.Count);
                string baseOp = ArrayBaseOperand(arg.ArrayName);
                for (int i = 0; i < elemSize; ++i)
                {
                    LoadExtendedByteIntoW(arg.Src, i);
                    string cell = OffsetOperand(baseOp, ci.Value * elemSize + i);
                    SelectBank(cell);
                    Emit("MOVWF", cell, GetAccessMode(cell));
                }

                return;
            }

            EmitArrayElementPointer(arg.ArrayName, arg.Index, elemSize);
            if (elemSize == 1)
            {
                LoadIntoW(arg.Src);
                Emit("MOVWF", "INDF0", "ACCESS");
                return;
            }

            LoadExtendedByteIntoW(arg.Src, 0);
            Emit("MOVWF", "POSTINC0", "ACCESS");
            LoadExtendedByteIntoW(arg.Src, 1);
            Emit("MOVWF", "INDF0", "ACCESS");
        }

        private void EmitPointerIntoFsr0(Val ptr)
        {
            if (ptr is Constant c)
            {
                Emit("LFSR", "0", $"0x{c.Value:X3}");
                return;
            }

            if (ptr is ArrayBase ab)
            {
                Emit("LFSR", "0", ArrayBaseOperand(ab.ArrayName));
                return;
            }

            string addr = ResolveAddress(ptr);
            Emit("MOVFF", addr, "FSR0L");
            if (WidthOf(ptr) > 1) Emit("MOVFF", ByteOf(ptr, 1), "FSR0H");
            else Emit("CLRF", "FSR0H", "ACCESS");
        }

        private void EmitBytearrayPointerIntoFsr0(string ptrName, Val index)
        {
            string slot = stackLayout.ContainsKey(ptrName) ? ptrName : GetOrAllocVariable(ptrName);
            Emit("MOVFF", slot, "FSR0L");
            Emit("MOVFF", OffsetOperand(slot, 1), "FSR0H");
            LoadIntoW(index);
            Emit("ADDWF", "FSR0L", "F", "ACCESS");
            Emit("MOVLW", "0x00");
            Emit("ADDWFC", "FSR0H", "F", "ACCESS");
        }

        private int IndirectWidth(DataType elem, Val other) =>
            elem != DataType.UINT8 ? elem.SizeOf() : WidthOf(other);

        private static string FlashLabel(string name) => "__flash_" + name.Replace('.', '_');

        private const int FlashBytesPerLine = 8;

        private readonly List<(string Name, List<int> Bytes)> flashData = new();
        private readonly HashSet<string> flashDataNames = new();

        private void CompileFlashData(FlashData arg)
        {
            if (!flashDataNames.Add(arg.Name)) return;
            flashData.Add((arg.Name, arg.Bytes));
        }

        private void EmitFlashTables()
        {
            if (flashData.Count == 0) return;

            EmitComment("--- Flash tables (TBLRD, byte-addressed program memory) ---");
            foreach (var (name, bytes) in flashData)
            {
                EmitLabel(FlashLabel(name));
                for (int i = 0; i < bytes.Count; i += FlashBytesPerLine)
                {
                    var chunk = bytes.Skip(i).Take(FlashBytesPerLine).Select(b => $"0x{b & 0xFF:X2}");
                    EmitRaw($"\tDB {string.Join(", ", chunk)}");
                }
            }
        }

        private void EmitTablePointerAdvance(Val index)
        {
            LoadIntoW(index);
            Emit("ADDWF", "TBLPTRL", "F", "ACCESS");
            Emit("MOVLW", "0x00");
            Emit("ADDWFC", "TBLPTRH", "F", "ACCESS");
            Emit("ADDWFC", "TBLPTRU", "F", "ACCESS");
            Emit("TBLRD*");
            Emit("MOVF", "TABLAT", "W", "ACCESS");
        }

        private void CompileArrayLoadFlash(ArrayLoadFlash arg)
        {
            string tbl = FlashLabel(arg.ArrayName);
            Emit("MOVLW", $"LOW({tbl})");
            Emit("MOVWF", "TBLPTRL", "ACCESS");
            Emit("MOVLW", $"HIGH({tbl})");
            Emit("MOVWF", "TBLPTRH", "ACCESS");
            Emit("MOVLW", $"UPPER({tbl})");
            Emit("MOVWF", "TBLPTRU", "ACCESS");
            EmitTablePointerAdvance(arg.Index);
            StoreWInto(arg.Dst);
        }

        private void CompileFlashLoadPtr(FlashLoadPtr arg)
        {
            if (arg.Ptr is FlashStrAddr fsa)
            {
                CompileArrayLoadFlash(new ArrayLoadFlash(fsa.Name, arg.Index, arg.Dst));
                return;
            }

            string lo = ResolveAddress(arg.Ptr);
            Emit("MOVFF", lo, "TBLPTRL");
            Emit("MOVFF", OffsetOperand(lo, 1), "TBLPTRH");
            Emit("CLRF", "TBLPTRU", "ACCESS");
            EmitTablePointerAdvance(arg.Index);
            StoreWInto(arg.Dst);
        }

        private void CompileLoadIndirect(LoadIndirect li)
        {
            int width = IndirectWidth(li.Elem, li.Dst);
            int dstWidth = WidthOf(li.Dst);
            EmitPointerIntoFsr0(li.SrcPtr);

            for (int i = 0; i < width; ++i)
            {
                Emit("MOVF", i == width - 1 ? "INDF0" : "POSTINC0", "W", "ACCESS");
                if (i >= dstWidth) continue;
                if (dstWidth == 1) StoreWInto(li.Dst);
                else StoreWIntoByte(li.Dst, i);
            }
        }

        private void CompileStoreIndirect(StoreIndirect si)
        {
            int width = IndirectWidth(si.Elem, si.Src);
            EmitPointerIntoFsr0(si.DstPtr);

            for (int i = 0; i < width; ++i)
            {
                LoadExtendedByteIntoW(si.Src, i);
                Emit("MOVWF", i == width - 1 ? "INDF0" : "POSTINC0", "ACCESS");
            }
        }

        private void CompileBytearrayLoad(BytearrayLoad bl)
        {
            EmitBytearrayPointerIntoFsr0(bl.PtrName, bl.Index);
            Emit("MOVF", "INDF0", "W", "ACCESS");
            StoreWInto(bl.Dst);
        }

        private void CompileBytearrayStore(BytearrayStore bs)
        {
            EmitBytearrayPointerIntoFsr0(bs.PtrName, bs.Index);
            LoadIntoW(bs.Src);
            Emit("MOVWF", "INDF0", "ACCESS");
        }
}