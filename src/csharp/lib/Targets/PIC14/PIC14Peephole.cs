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

namespace PyMCU.Backend.Targets.PIC14;

// ---------------------------------------------------------------------------
// AsmLine
// ---------------------------------------------------------------------------

public class PIC14AsmLine
{
    public enum LineType
    {
        Instruction,
        Label,
        Comment,
        Raw,
        Empty
    }

    public LineType Type;
    public string LabelText = "";
    public string Mnemonic = "";
    public string Op1 = "";
    public string Op2 = "";
    public string Content = "";

    public static PIC14AsmLine MakeInstruction(string m, string o1 = "", string o2 = "")
        => new() { Type = LineType.Instruction, Mnemonic = m, Op1 = o1, Op2 = o2 };

    public static PIC14AsmLine MakeLabel(string l)
        => new() { Type = LineType.Label, LabelText = l };

    public static PIC14AsmLine MakeComment(string c)
        => new() { Type = LineType.Comment, Content = c };

    public static PIC14AsmLine MakeRaw(string r)
        => new() { Type = LineType.Raw, Content = r };

    public static PIC14AsmLine MakeEmpty()
        => new() { Type = LineType.Empty };

    public override string ToString()
    {
        switch (Type)
        {
            case LineType.Instruction:
                if (string.IsNullOrEmpty(Op1)) return $"\t{Mnemonic}";
                if (string.IsNullOrEmpty(Op2)) return $"\t{Mnemonic}\t{Op1}";
                return $"\t{Mnemonic}\t{Op1}, {Op2}";
            case LineType.Label:
                return $"{LabelText}:";
            case LineType.Comment:
                return $"; {Content}";
            case LineType.Raw:
                return Content;
            default:
                return "";
        }
    }
}

// ---------------------------------------------------------------------------
// Architecture Strategy (Abstract + Concrete implementations)
// ---------------------------------------------------------------------------

public abstract class ArchStrategy
{
    // Preamble: emits global directives like LIST, INCLUDE, CONFIG
    public abstract void EmitPreamble();

    // Banking: emits instructions to select the bank for 'bank'
    public abstract void EmitBankSelect(int bank);

    // Invalidates internal bank tracking (e.g. after CALL)
    public virtual void InvalidateBank()
    {
    }

    // ISR support
    public abstract void EmitContextSave();
    public abstract void EmitContextRestore();
    public abstract void EmitInterruptReturn();

    // --- Indirect addressing -------------------------------------------------
    //
    // The two PIC14 cores expose very different hardware here:
    //
    //   legacy   one 8-bit FSR (0x04) whose 9th address bit is STATUS<IRP>,
    //            read/written through INDF (0x00); the pointer is advanced with
    //            INCF FSR, F.
    //   enhanced two 16-bit index registers FSR0/FSR1 (FSR0L 0x04 / FSR0H 0x05)
    //            read/written through INDF0 (0x00) / INDF1; no IRP bit at all,
    //            plus MOVIW/MOVWI (pre/post increment and decrement) and ADDFSR.
    //
    // Emitting the legacy mnemonics on a PIC16F1xxxx does not just run wrong, it
    // does not even assemble: "FSR" and "INDF" are not defined in its header.
    // Every indirect access therefore goes through the operations below so the
    // generator itself stays core-agnostic. The backend only ever uses one index
    // register (FSR0 on the enhanced core), matching the single FSR of the legacy
    // one; FSR1 is left free for future use (e.g. memcpy-style loops).

    /// <summary>Points the index register at the RAM address held in W.</summary>
    /// <param name="clearHigh">
    /// Ask for the high half of the pointer to be forced to zero, i.e. the target
    /// is known to live below 0x100. The enhanced core always writes the high half
    /// regardless: FSR0H is a real register with no implicit value, so leaving a
    /// stale one behind would aim the pointer at an arbitrary bank.
    /// </param>
    public abstract void EmitPointerFromW(bool clearHigh);

    /// <summary>Points the index register at a compile-time RAM address.</summary>
    public abstract void EmitPointerFromConst(int address);

    /// <summary>W = *pointer.</summary>
    public abstract void EmitIndirectLoadW();

    /// <summary>*pointer = W.</summary>
    public abstract void EmitIndirectStoreW();

    /// <summary>*pointer = 0.</summary>
    public abstract void EmitIndirectClear();

    /// <summary>*pointer = W, then advance the pointer one byte.</summary>
    public abstract void EmitIndirectStoreWAndAdvance();

    /// <summary>Advance the pointer one byte, then W = *pointer.</summary>
    public abstract void EmitAdvanceAndIndirectLoadW();
}

// Legacy PIC14 Architecture (e.g. PIC16F84A, PIC16F877A)
public class PIC14Strategy : ArchStrategy
{
    private readonly PIC14CodeGen _codegen;

    public PIC14Strategy(PIC14CodeGen codegen)
    {
        _codegen = codegen;
    }

    public override void EmitPreamble()
    {
        _codegen.EmitRaw($"\tLIST P={_codegen.Config.TargetChip}");
        string chipShort = _codegen.Config.TargetChip;
        if (chipShort.StartsWith("pic"))
            chipShort = chipShort.Substring(3);
        _codegen.EmitRaw($"#include <p{chipShort}.inc>");
        _codegen.EmitConfigDirectives();
    }

    public override void EmitBankSelect(int bank)
    {
        // Legacy BCF/BSF method
        if ((bank & 1) != 0)
            _codegen.Emit("BSF", "STATUS", "5");
        else
            _codegen.Emit("BCF", "STATUS", "5"); // RP0
        if ((bank & 2) != 0)
            _codegen.Emit("BSF", "STATUS", "6");
        else
            _codegen.Emit("BCF", "STATUS", "6"); // RP1
    }

    public override void EmitContextSave()
    {
        _codegen.EmitComment("Context Save (Manual)");
        _codegen.Emit("MOVWF", "W_TEMP");
        _codegen.Emit("SWAPF", "STATUS", "W");
        _codegen.Emit("MOVWF", "STATUS_TEMP");
        // Force Bank 0 (Manual BCFs because we don't track bank in ISR entry yet)
        _codegen.Emit("BCF", "STATUS", "5");
        _codegen.Emit("BCF", "STATUS", "6");
    }

    public override void EmitContextRestore()
    {
        _codegen.EmitComment("Context Restore (Manual)");
        _codegen.Emit("SWAPF", "STATUS_TEMP", "W");
        _codegen.Emit("MOVWF", "STATUS");
        _codegen.Emit("SWAPF", "W_TEMP", "F");
        _codegen.Emit("SWAPF", "W_TEMP", "W");
    }

    public override void EmitInterruptReturn() => _codegen.Emit("RETFIE");

    // --- Indirect addressing: FSR / INDF + STATUS<IRP> ---
    //
    // IRP is only cleared where the generator historically cleared it (the array
    // paths, whose storage is known to sit below 0x100). The pointer paths leave
    // it alone, so a ptr() dereference inherits whatever IRP the last array access
    // left behind — harmless today because every allocation lives in bank 0, but a
    // latent bug the moment the allocator can place data above 0xFF.

    public override void EmitPointerFromW(bool clearHigh)
    {
        _codegen.Emit("MOVWF", "FSR");
        if (clearHigh)
            _codegen.Emit("BCF", "STATUS", "7"); // IRP = 0
    }

    public override void EmitPointerFromConst(int address)
    {
        _codegen.Emit("MOVLW", $"0x{address & 0xFF:X2}");
        _codegen.Emit("MOVWF", "FSR");
    }

    public override void EmitIndirectLoadW() => _codegen.Emit("MOVF", "INDF", "W");

    public override void EmitIndirectStoreW() => _codegen.Emit("MOVWF", "INDF");

    public override void EmitIndirectClear() => _codegen.Emit("CLRF", "INDF");

    public override void EmitIndirectStoreWAndAdvance()
    {
        _codegen.Emit("MOVWF", "INDF");
        _codegen.Emit("INCF", "FSR", "F");
    }

    public override void EmitAdvanceAndIndirectLoadW()
    {
        _codegen.Emit("INCF", "FSR", "F");
        _codegen.Emit("MOVF", "INDF", "W");
    }
}

// Enhanced PIC14E Architecture (e.g. PIC16F1xxxx)
public class PIC14EStrategy : ArchStrategy
{
    private readonly PIC14CodeGen _codegen;
    private int _currentBsr = -1;

    public PIC14EStrategy(PIC14CodeGen codegen)
    {
        _codegen = codegen;
    }

    public override void EmitPreamble()
    {
        _codegen.EmitRaw($"\tLIST P={_codegen.Config.TargetChip}");
        string chipShort = _codegen.Config.TargetChip;
        if (chipShort.StartsWith("pic"))
            chipShort = chipShort.Substring(3);
        _codegen.EmitRaw($"#include <p{chipShort}.inc>");
        // ErrorLevel suppression often useful for generated code
        _codegen.EmitRaw("\terrorlevel -302");
        _codegen.EmitConfigDirectives();
    }

    public override void EmitBankSelect(int bank)
    {
        // Optimization: Track current_bsr to avoid redundant MOVLB
        if (_currentBsr != bank)
        {
            _codegen.Emit("MOVLB", $"0x{bank:X2}");
            _currentBsr = bank;
        }
    }

    public override void InvalidateBank() => _currentBsr = -1;

    public override void EmitContextSave()
    {
        // Hardware Context Save (Shadow Registers) — no instructions needed.
        _codegen.EmitComment("Context Save (Hardware Automatic)");
    }

    public override void EmitContextRestore()
    {
        // Hardware Context Restore (Shadow Registers) — no instructions needed (RETFIE restores).
        _codegen.EmitComment("Context Restore (Hardware Automatic)");
    }

    public override void EmitInterruptReturn() => _codegen.Emit("RETFIE");

    // --- Indirect addressing: FSR0 / INDF0 -----------------------------------
    //
    // Why the traditional (banked) view and not the linear data memory at 0x2000
    // ------------------------------------------------------------------------
    // FSR0 is a 16-bit pointer with two views of RAM: 0x0000-0x0FFF is the
    // traditional map (the same flat file address the direct-addressing EQUs
    // use), and 0x2000-0x29AF is the linear map, which stitches the per-bank GPR
    // windows into one gap-free run.
    //
    // The generator addresses arrays as "MOVLW index / ADDLW <array>" — 8-bit
    // arithmetic on the array's flat file address — so an array is by
    // construction reachable below 0x100 and never straddles a bank boundary.
    // Under that layout the traditional view is an exact match: the address
    // already in W is the address FSR0 wants, and pointing at it costs one MOVWF
    // plus one CLRF. Switching to the linear map would mean rebasing every access
    // (linear = 0x2000 + file - 0x20 only holds inside bank 0) for no gain.
    //
    // The linear map earns its keep the day the stack allocator can spread data
    // past bank 0's GPR window (0x20-0x6F on these parts): a >80-byte array only
    // becomes contiguous — and index arithmetic only stays a plain add — in the
    // linear view. That is an allocator change, not a codegen one, so it is
    // deliberately not anticipated here.

    public override void EmitPointerFromW(bool clearHigh)
    {
        // clearHigh is ignored on purpose: unlike STATUS<IRP>, FSR0H has no
        // "default" state to fall back on, so it must always be written.
        _codegen.Emit("MOVWF", "FSR0L");
        _codegen.Emit("CLRF", "FSR0H");
    }

    public override void EmitPointerFromConst(int address)
    {
        _codegen.Emit("MOVLW", $"0x{address & 0xFF:X2}");
        _codegen.Emit("MOVWF", "FSR0L");

        // 16-bit pointer: unlike the legacy core, an address above 0xFF is
        // reachable, so the high byte is kept instead of being truncated away.
        int high = (address >> 8) & 0xFF;
        if (high == 0)
        {
            _codegen.Emit("CLRF", "FSR0H");
        }
        else
        {
            _codegen.Emit("MOVLW", $"0x{high:X2}");
            _codegen.Emit("MOVWF", "FSR0H");
        }
    }

    public override void EmitIndirectLoadW() => _codegen.Emit("MOVF", "INDF0", "W");

    public override void EmitIndirectStoreW() => _codegen.Emit("MOVWF", "INDF0");

    public override void EmitIndirectClear() => _codegen.Emit("CLRF", "INDF0");

    // The two fused forms below are exactly what MOVWI/MOVIW were added for: the
    // store/load and the pointer bump collapse into a single instruction. Where
    // the bump stands on its own the generator does not need it, so ADDFSR (which
    // also leaves Z alone, unlike the legacy INCF) never has to appear.

    public override void EmitIndirectStoreWAndAdvance() => _codegen.Emit("MOVWI", "FSR0++");

    public override void EmitAdvanceAndIndirectLoadW() => _codegen.Emit("MOVIW", "++FSR0");
}

// ---------------------------------------------------------------------------
// Peephole Optimizer
// ---------------------------------------------------------------------------

public static class PIC14Peephole
{

    private static bool TryParseLiteral(string text, out int value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        return int.TryParse(text, out value);
    }
    private static List<PIC14AsmLine> CoalesceBitOps(List<PIC14AsmLine> lines)
    {
        var result = new List<PIC14AsmLine>();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Type == PIC14AsmLine.LineType.Instruction &&
                (line.Mnemonic == "BSF" || line.Mnemonic == "BCF"))
            {
                string reg = line.Op1;
                string mnemonic = line.Mnemonic;

                if (reg == "STATUS")
                {
                    result.Add(line);
                    continue;
                }

                var bits = new List<int>();
                int j = i;
                while (j < lines.Count)
                {
                    if (lines[j].Type == PIC14AsmLine.LineType.Instruction)
                    {
                        if (lines[j].Mnemonic == mnemonic && lines[j].Op1 == reg)
                        {
                            if (int.TryParse(lines[j].Op2, out int bit))
                            {
                                bits.Add(bit);
                                j++;
                            }
                            else break;
                        }
                        else break;
                    }
                    else if (lines[j].Type == PIC14AsmLine.LineType.Comment ||
                             lines[j].Type == PIC14AsmLine.LineType.Empty)
                    {
                        j++;
                    }
                    else break;
                }

                if (bits.Count >= 3)
                {
                    int mask = 0;
                    foreach (int b in bits) mask |= (1 << b);

                    if (mnemonic == "BSF")
                    {
                        result.Add(PIC14AsmLine.MakeInstruction("MOVLW", $"0x{mask:X2}"));
                        result.Add(PIC14AsmLine.MakeInstruction("IORWF", reg, "F"));
                    }
                    else
                    {
                        int invMask = (~mask) & 0xFF;
                        result.Add(PIC14AsmLine.MakeInstruction("MOVLW", $"0x{invMask:X2}"));
                        result.Add(PIC14AsmLine.MakeInstruction("ANDWF", reg, "F"));
                    }

                    i = j - 1;
                }
                else
                {
                    result.Add(line);
                }
            }
            else
            {
                result.Add(line);
            }
        }

        return result;
    }

    /// <summary>
    /// <c>PYMCU_NO_PEEPHOLE=1</c> hands the assembly straight through, so a miscompile
    /// can be bisected to this file (wrong only after the peephole) versus the code
    /// generator (already wrong before it).
    /// </summary>
    private static bool Bypassed =>
        Environment.GetEnvironmentVariable("PYMCU_NO_PEEPHOLE") == "1";

    public static List<PIC14AsmLine> Optimize(List<PIC14AsmLine> lines)
    {
        if (Bypassed) return new List<PIC14AsmLine>(lines);

        var result = new List<PIC14AsmLine>(CoalesceBitOps(lines));
        bool changed = true;

        while (changed)
        {
            changed = false;

            // --- Dead Label Elimination ---
            var usedLabels = new HashSet<string>();
            foreach (var line in result)
            {
                if (line.Type == PIC14AsmLine.LineType.Instruction)
                {
                    if (line.Mnemonic == "GOTO" || line.Mnemonic == "CALL")
                        usedLabels.Add(line.Op1);
                }
            }

            // Entry points and special labels
            usedLabels.Add("main");
            usedLabels.Add("__interrupt");

            var next = new List<PIC14AsmLine>();
            string? wLit = null;
            string? wVar = null;
            bool? rp0 = null;
            bool? rp1 = null;
            int? currentMovlb = null; // Track MOVLB for PIC14E

            for (int i = 0; i < result.Count; i++)
            {
                var current = result[i];

                // --- Copy Propagation ---
                // Pattern: MOVWF T -> MOVF T, W -> MOVWF D
                // Optimization: skip the first two, keep MOVWF D
                if (current.Type == PIC14AsmLine.LineType.Instruction &&
                    current.Mnemonic == "MOVWF" && current.Op1.StartsWith("tmp."))
                {
                    string tmpReg = current.Op1;
                    int j = i + 1;

                    void Skip1()
                    {
                        while (j < result.Count &&
                               (result[j].Type == PIC14AsmLine.LineType.Comment ||
                                result[j].Type == PIC14AsmLine.LineType.Empty))
                            j++;
                    }

                    Skip1();

                    if (j < result.Count &&
                        result[j].Type == PIC14AsmLine.LineType.Instruction &&
                        result[j].Mnemonic == "MOVF" && result[j].Op1 == tmpReg &&
                        result[j].Op2 == "W")
                    {
                        int k = j + 1;
                        while (k < result.Count &&
                               (result[k].Type == PIC14AsmLine.LineType.Comment ||
                                result[k].Type == PIC14AsmLine.LineType.Empty))
                            k++;

                        if (k < result.Count &&
                            result[k].Type == PIC14AsmLine.LineType.Instruction &&
                            result[k].Mnemonic == "MOVWF")
                        {
                            i = j; // Skip MOVWF T and MOVF T,W
                            changed = true;
                            wVar = null;
                            continue;
                        }
                    }
                }

                // --- Comparison to Jump Optimization ---
                if (current.Type == PIC14AsmLine.LineType.Instruction &&
                    current.Mnemonic == "CLRF" && current.Op1.StartsWith("tmp."))
                {
                    string tmpReg = current.Op1;
                    int j = i + 1;

                    void Skip2()
                    {
                        while (j < result.Count &&
                               (result[j].Type == PIC14AsmLine.LineType.Comment ||
                                result[j].Type == PIC14AsmLine.LineType.Empty))
                            j++;
                    }

                    Skip2();
                    if (j < result.Count &&
                        result[j].Type == PIC14AsmLine.LineType.Instruction &&
                        (result[j].Mnemonic == "BTFSS" || result[j].Mnemonic == "BTFSC") &&
                        result[j].Op1 == "STATUS")
                    {
                        var i1 = result[j++];
                        Skip2();
                        if (j < result.Count &&
                            result[j].Type == PIC14AsmLine.LineType.Instruction &&
                            result[j].Mnemonic == "INCF" && result[j].Op1 == tmpReg &&
                            result[j].Op2 == "F")
                        {
                            j++;
                            Skip2();
                            if (j < result.Count &&
                                result[j].Type == PIC14AsmLine.LineType.Instruction &&
                                result[j].Mnemonic == "MOVF" && result[j].Op1 == tmpReg &&
                                result[j].Op2 == "W")
                            {
                                j++;
                                Skip2();
                                if (j < result.Count &&
                                    result[j].Type == PIC14AsmLine.LineType.Instruction &&
                                    result[j].Mnemonic == "IORLW" && result[j].Op1 == "0")
                                {
                                    j++;
                                    Skip2();
                                }

                                if (j + 1 < result.Count &&
                                    result[j].Type == PIC14AsmLine.LineType.Instruction &&
                                    (result[j].Mnemonic == "BTFSS" || result[j].Mnemonic == "BTFSC") &&
                                    result[j].Op1 == "STATUS" && result[j].Op2 == "2" &&
                                    result[j + 1].Type == PIC14AsmLine.LineType.Instruction &&
                                    result[j + 1].Mnemonic == "GOTO")
                                {
                                    bool bitIsSc = (i1.Mnemonic == "BTFSC");
                                    bool zIsSc = (result[j].Mnemonic == "BTFSC");
                                    string newMnemonic = (bitIsSc == zIsSc) ? "BTFSS" : "BTFSC";

                                    next.Add(PIC14AsmLine.MakeInstruction(newMnemonic, "STATUS", i1.Op2));
                                    next.Add(result[j + 1]);
                                    i = j + 1;
                                    changed = true;
                                    continue;
                                }
                            }
                        }
                    }
                }

                if (current.Type == PIC14AsmLine.LineType.Label)
                {
                    if (!usedLabels.Contains(current.LabelText) &&
                        (current.LabelText.StartsWith("L.") ||
                         current.LabelText.StartsWith("L_")))
                    {
                        changed = true;
                        continue;
                    }

                    wLit = null;
                    wVar = null;
                    rp0 = null;
                    rp1 = null;
                    currentMovlb = null;
                    next.Add(current);
                    continue;
                }

                if (current.Type != PIC14AsmLine.LineType.Instruction)
                {
                    next.Add(current);
                    continue;
                }

                // --- Bank tracking (PIC14E MOVLB) ---
                if (current.Mnemonic == "MOVLB")
                {
                    if (TryParseLiteral(current.Op1, out int bank))
                    {
                        if (currentMovlb.HasValue && currentMovlb.Value == bank)
                        {
                            changed = true;
                            continue; // Skip redundant MOVLB
                        }

                        currentMovlb = bank;
                    }
                    else
                    {
                        currentMovlb = null;
                    }

                    next.Add(current);
                    continue;
                }

                // --- Bank tracking (Legacy PIC14 RP0/RP1) ---
                if (current.Mnemonic == "BSF" && current.Op1 == "STATUS" && current.Op2 == "5")
                {
                    if (rp0.HasValue && rp0.Value == true)
                    {
                        changed = true;
                        continue;
                    }

                    rp0 = true;
                }
                else if (current.Mnemonic == "BCF" && current.Op1 == "STATUS" && current.Op2 == "5")
                {
                    if (rp0.HasValue && rp0.Value == false)
                    {
                        changed = true;
                        continue;
                    }

                    rp0 = false;
                }
                else if (current.Mnemonic == "BSF" && current.Op1 == "STATUS" && current.Op2 == "6")
                {
                    if (rp1.HasValue && rp1.Value == true)
                    {
                        changed = true;
                        continue;
                    }

                    rp1 = true;
                }
                else if (current.Mnemonic == "BCF" && current.Op1 == "STATUS" && current.Op2 == "6")
                {
                    if (rp1.HasValue && rp1.Value == false)
                    {
                        changed = true;
                        continue;
                    }

                    rp1 = false;
                }

                // --- State tracking optimizations ---

                if (current.Mnemonic == "MOVLW")
                {
                    if (wLit != null && wLit == current.Op1)
                    {
                        changed = true;
                        continue;
                    }

                    wLit = current.Op1;
                    wVar = null;
                }
                else if (current.Mnemonic == "MOVF" && current.Op2 == "W")
                {
                    if (wVar != null && wVar == current.Op1)
                    {
                        changed = true;
                        continue;
                    }

                    wVar = current.Op1;
                    wLit = null;
                }
                else if (current.Mnemonic == "MOVWF")
                {
                    if (wVar != null && wVar == current.Op1)
                    {
                        changed = true;
                        continue;
                    }

                    wVar = current.Op1;
                    // wLit remains valid!
                }
                else if (current.Mnemonic == "CLRF")
                {
                    if (wLit != null && (wLit == "0" || wLit == "0x00"))
                        wVar = current.Op1;
                    // else: we don't know what's in x relative to W
                }
                else if (current.Mnemonic == "IORLW" && current.Op1 == "0")
                {
                    bool redundant = false;
                    for (int j = next.Count - 1; j >= 0; j--)
                    {
                        if (next[j].Type == PIC14AsmLine.LineType.Label) break;
                        if (next[j].Type == PIC14AsmLine.LineType.Instruction)
                        {
                            string m = next[j].Mnemonic;
                            if (m == "MOVF" || m == "ADDWF" || m == "SUBWF" || m == "ANDWF" ||
                                m == "IORWF" || m == "XORWF" || m == "INCF" || m == "DECF" ||
                                m == "ADDLW" || m == "SUBLW" || m == "ANDLW" || m == "XORLW" ||
                                m == "CLRF" || m == "CLRW")
                                redundant = true;
                            break;
                        }
                    }

                    if (redundant || (wLit != null && (wLit == "0" || wLit == "0x00")))
                    {
                        changed = true;
                        continue;
                    }
                }
                else if (current.Mnemonic == "ADDLW" && (current.Op1 == "0" || current.Op1 == "0x00"))
                {
                    changed = true;
                    continue;
                }
                else if (current.Mnemonic == "XORLW" && (current.Op1 == "0" || current.Op1 == "0x00"))
                {
                    changed = true;
                    continue;
                }
                else if (current.Mnemonic == "ANDLW" && (current.Op1 == "255" || current.Op1 == "0xFF"))
                {
                    changed = true;
                    continue;
                }
                else if (current.Mnemonic == "BSF" || current.Mnemonic == "BCF")
                {
                    // Bit coalescing/redundancy
                    bool redundant = false;
                    for (int j = next.Count - 1; j >= 0; j--)
                    {
                        if (next[j].Type == PIC14AsmLine.LineType.Label) break;
                        if (next[j].Type == PIC14AsmLine.LineType.Instruction)
                        {
                            if (next[j].Op1 == current.Op1 && next[j].Op2 == current.Op2)
                            {
                                if (next[j].Mnemonic == "BSF" || next[j].Mnemonic == "BCF")
                                {
                                    if (next[j].Mnemonic == current.Mnemonic)
                                        redundant = true;
                                }
                            }

                            break;
                        }
                    }

                    if (redundant)
                    {
                        changed = true;
                        continue;
                    }

                    if (next.Count > 0 &&
                        next[next.Count - 1].Type == PIC14AsmLine.LineType.Instruction &&
                        next[next.Count - 1].Op1 == current.Op1 &&
                        next[next.Count - 1].Op2 == current.Op2 &&
                        (next[next.Count - 1].Mnemonic == "BSF" || next[next.Count - 1].Mnemonic == "BCF"))
                    {
                        if (next[next.Count - 1].Mnemonic != current.Mnemonic)
                        {
                            next.RemoveAt(next.Count - 1);
                            changed = true;
                        }
                    }
                }
                else if (current.Mnemonic == "GOTO")
                {
                    bool redundant = false;
                    for (int j = i + 1; j < result.Count; j++)
                    {
                        if (result[j].Type == PIC14AsmLine.LineType.Label)
                        {
                            if (result[j].LabelText == current.Op1)
                            {
                                redundant = true;
                                break;
                            }
                        }
                        else if (result[j].Type == PIC14AsmLine.LineType.Comment ||
                                 result[j].Type == PIC14AsmLine.LineType.Empty)
                            continue;
                        else
                            break;
                    }

                    bool precededBySkip = false;
                    if (next.Count > 0 && next[next.Count - 1].Type == PIC14AsmLine.LineType.Instruction)
                    {
                        string prev = next[next.Count - 1].Mnemonic;
                        if (prev == "BTFSC" || prev == "BTFSS" || prev == "DECFSZ" || prev == "INCFSZ")
                            precededBySkip = true;
                    }

                    if (redundant)
                    {
                        changed = true;
                        if (precededBySkip)
                            next.RemoveAt(next.Count - 1);
                        continue;
                    }

                    next.Add(current);

                    if (!precededBySkip)
                    {
                        while (i + 1 < result.Count &&
                               result[i + 1].Type != PIC14AsmLine.LineType.Label)
                        {
                            if (result[i + 1].Type == PIC14AsmLine.LineType.Instruction)
                                changed = true;
                            else
                                next.Add(result[i + 1]);
                            i++;
                        }
                    }

                    wLit = null;
                    wVar = null;
                    rp0 = null;
                    rp1 = null;
                    currentMovlb = null;
                    continue;
                }
                else if (current.Mnemonic == "RETURN" || current.Mnemonic == "RETFIE")
                {
                    bool precededBySkip = false;
                    if (next.Count > 0 && next[next.Count - 1].Type == PIC14AsmLine.LineType.Instruction)
                    {
                        string prev = next[next.Count - 1].Mnemonic;
                        if (prev == "BTFSC" || prev == "BTFSS" || prev == "DECFSZ" || prev == "INCFSZ")
                            precededBySkip = true;
                    }

                    next.Add(current);
                    if (!precededBySkip)
                    {
                        while (i + 1 < result.Count &&
                               result[i + 1].Type != PIC14AsmLine.LineType.Label)
                        {
                            if (result[i + 1].Type == PIC14AsmLine.LineType.Instruction)
                                changed = true;
                            else
                                next.Add(result[i + 1]);
                            i++;
                        }
                    }

                    wLit = null;
                    wVar = null;
                    rp0 = null;
                    rp1 = null;
                    currentMovlb = null;
                    continue;
                }
                else if (current.Mnemonic == "CALL")
                {
                    wLit = null;
                    wVar = null;
                    rp0 = null;
                    rp1 = null;
                    currentMovlb = null;
                }
                else
                {
                    // Arithmetic instructions typically change W and flags
                    wLit = null;
                    wVar = null;
                }

                // --- INCF/DECF Optimization ---
                // Pattern 1: MOVF x, W -> ADDLW 1 -> MOVWF x  =>  INCF x, F
                // Pattern 2: MOVF x, W -> ADDLW 1 -> MOVWF tmp -> MOVF tmp, W -> MOVWF x  =>  INCF x, F
                if (current.Mnemonic == "MOVF" && current.Op2 == "W")
                {
                    string reg = current.Op1;

                    PIC14AsmLine? NextInst(ref int idx)
                    {
                        while (idx < result.Count &&
                               (result[idx].Type == PIC14AsmLine.LineType.Comment ||
                                result[idx].Type == PIC14AsmLine.LineType.Empty))
                            idx++;
                        if (idx < result.Count && result[idx].Type == PIC14AsmLine.LineType.Instruction)
                            return result[idx];
                        return null;
                    }

                    int idx2 = i + 1;
                    var inst2 = NextInst(ref idx2);

                    if (inst2 != null)
                    {
                        bool isInc = false, isDec = false;
                        if (inst2.Mnemonic == "ADDLW")
                        {
                            if (inst2.Op1 == "1" || inst2.Op1 == "0x01") isInc = true;
                            else if (inst2.Op1 == "255" || inst2.Op1 == "0xFF" || inst2.Op1 == "-1") isDec = true;
                        }

                        if (isInc || isDec)
                        {
                            int idx3 = idx2 + 1;
                            var inst3 = NextInst(ref idx3);

                            if (inst3 != null)
                            {
                                // Case 1: Direct move back to x
                                if (inst3.Mnemonic == "MOVWF" && inst3.Op1 == reg)
                                {
                                    string newMnemonic = isInc ? "INCF" : "DECF";
                                    next.Add(PIC14AsmLine.MakeInstruction(newMnemonic, reg, "F"));
                                    wLit = null;
                                    wVar = null;
                                    i = idx3;
                                    changed = true;
                                    continue;
                                }
                                // Case 2: Move to temp, then move temp back to x
                                else if (inst3.Mnemonic == "MOVWF" && inst3.Op1.StartsWith("tmp."))
                                {
                                    string tmpReg = inst3.Op1;
                                    int idx4 = idx3 + 1;
                                    var inst4 = NextInst(ref idx4);

                                    if (inst4 != null && inst4.Mnemonic == "MOVF" &&
                                        inst4.Op1 == tmpReg && inst4.Op2 == "W")
                                    {
                                        int idx5 = idx4 + 1;
                                        var inst5 = NextInst(ref idx5);

                                        if (inst5 != null && inst5.Mnemonic == "MOVWF" && inst5.Op1 == reg)
                                        {
                                            string newMnemonic = isInc ? "INCF" : "DECF";
                                            next.Add(PIC14AsmLine.MakeInstruction(newMnemonic, reg, "F"));
                                            wLit = null;
                                            wVar = null;
                                            i = idx5;
                                            changed = true;
                                            continue;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                next.Add(current);
            }

            result = next;
        }

        return result;
    }
}