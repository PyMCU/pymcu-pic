namespace PyMCU.Backend.Targets.PIC18;

/// <summary>
/// Chooses between the short and the long unconditional jump on PIC18.
///
/// `BRA` takes an 11-bit signed WORD offset, so it reaches about plus or minus 1024 words.
/// `GOTO` is the two-word absolute form. The backend emitted `BRA` for every jump and checked
/// nothing, so a function whose body grew past roughly 1K words stopped assembling: `asyncio`
/// on pic18f45k50 failed with three `Error[126] Argument out of range`, one at -1089, with the
/// coroutine dispatch branching over the whole body of the coroutine.
///
/// Runs after the peephole, because the peephole adds and removes instructions and a distance
/// measured before it is measured on code that is not what gets assembled.
/// </summary>
public static class PIC18BranchRelax
{
    // BRA's reach, in words, measured from the instruction after it.
    private const int BraMin = -1024;
    private const int BraMax = 1023;

    // The two-word instructions. Everything else this backend emits is one word.
    private static bool IsTwoWord(string mnemonic) =>
        mnemonic is "GOTO" or "CALL" or "MOVFF" or "LFSR";

    // A skip leaves the NEXT instruction as the one it may jump over, so widening that
    // instruction is a change to what the skip steps over. Not done here; see Relax.
    private static bool IsSkip(string mnemonic) =>
        mnemonic is "BTFSS" or "BTFSC" or "CPFSEQ" or "CPFSGT" or "CPFSLT"
                 or "TSTFSZ" or "DECFSZ" or "DCFSNZ" or "INCFSZ" or "INFSNZ";

    // Every mnemonic the backend emits, plus the ones the stdlib writes inside asm(). A Raw
    // line whose first token is not here cannot be sized, and its span is treated as
    // unmeasurable rather than guessed at.
    private static readonly HashSet<string> Sizable = new()
    {
        "MOVWF", "MOVF", "MOVLW", "BRA", "SUBWFB", "MOVLB", "IORWF", "SUBWF", "BTFSS",
        "BCF", "BSF", "RLCF", "RRCF", "ADDWFC", "BTFSC", "XORWF", "XORLW", "RETURN",
        "RETFIE", "CLRF", "ADDWF", "SUBLW", "ADDLW", "ANDLW", "ANDWF", "IORLW", "COMF",
        "INCF", "DECF", "NOP", "SETF", "SWAPF", "TSTFSZ", "DECFSZ", "INCFSZ", "DCFSNZ",
        "INFSNZ", "CPFSEQ", "CPFSGT", "CPFSLT", "BZ", "BNZ", "BC", "BNC", "BN", "BNN",
        "BOV", "BNOV", "RCALL", "PUSH", "POP", "SLEEP", "CLRWDT", "RESET", "DAW", "NEGF",
        "MULWF", "MULLW", "TBLRD", "TBLWT", "GOTO", "CALL", "MOVFF", "LFSR",
    };

    /// <summary>
    /// Rewrites every out-of-range `BRA &lt;label&gt;` into `GOTO &lt;label&gt;`, in place.
    /// Iterates to a fixed point: a rewrite grows the image by one word and can push another
    /// branch out of range. It only ever grows, so it terminates.
    /// </summary>
    public static void Apply(List<PIC18AsmLine> lines)
    {
        for (int pass = 0; pass < 16; pass++)
            if (!Relax(lines))
                return;

        throw new InvalidOperationException(
            "PIC18 branch relaxation did not settle in 16 passes. Each pass only ever turns a "
            + "one-word BRA into a two-word GOTO, so it cannot cycle; reaching this means the "
            + "word accounting is wrong rather than the program being large.");
    }

    /// <summary>
    /// Words a line occupies. `unmeasurable` is set for a Raw line this cannot size: an
    /// `#include`, or assembly text using something outside <see cref="Sizable"/>. The stdlib's
    /// PIC18 delay loops are written as one `asm()` per instruction, so ordinary Raw lines size
    /// exactly and only the genuinely unknown ones are flagged.
    /// </summary>
    private static int WordsOf(PIC18AsmLine line, out bool unmeasurable)
    {
        unmeasurable = false;
        switch (line.Type)
        {
            case PIC18AsmLine.LineType.Instruction:
                return IsTwoWord(line.Mnemonic) ? 2 : 1;

            case PIC18AsmLine.LineType.Raw:
            {
                int words = 0;
                foreach (var rawLine in line.Content.Split('\n'))
                {
                    string text = rawLine.Trim();
                    int comment = text.IndexOf(';');
                    if (comment >= 0) text = text[..comment].Trim();
                    if (text.Length == 0) continue;

                    string head = text.Split(new[] { ' ', '\t' }, 2)[0];
                    if (head.EndsWith(':')) continue;               // a label occupies nothing

                    string mnemonic = head.ToUpperInvariant();
                    if (!Sizable.Contains(mnemonic)) { unmeasurable = true; return 0; }
                    words += IsTwoWord(mnemonic) ? 2 : 1;
                }
                return words;
            }

            default:
                return 0;   // Label, Comment, Empty
        }
    }

    private static bool Relax(List<PIC18AsmLine> lines)
    {
        var offsetOf = new int[lines.Count];
        var opaqueAt = new List<int>();
        var labelAt = new Dictionary<string, int>();
        var labelLine = new Dictionary<string, int>();

        int at = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            offsetOf[i] = at;
            if (lines[i].Type == PIC18AsmLine.LineType.Label)
            {
                labelAt[lines[i].LabelText] = at;
                labelLine[lines[i].LabelText] = i;
            }
            at += WordsOf(lines[i], out bool unmeasurable);
            if (unmeasurable) opaqueAt.Add(i);
        }

        bool SpansOpaque(int a, int b)
        {
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            foreach (int o in opaqueAt) if (o > lo && o < hi) return true;
            return false;
        }

        int previousInstruction = -1;
        bool changed = false;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Type != PIC18AsmLine.LineType.Instruction) continue;

            if (line.Mnemonic != "BRA" || !labelAt.TryGetValue(line.Op1, out int target))
            {
                previousInstruction = i;
                continue;
            }

            // Offset is relative to the instruction AFTER the branch.
            int delta = target - (offsetOf[i] + 1);
            bool measurable = !SpansOpaque(i, labelLine[line.Op1]);
            bool outOfRange = delta < BraMin || delta > BraMax;
            bool skipped = previousInstruction >= 0 && IsSkip(lines[previousInstruction].Mnemonic);

            if (measurable && outOfRange && skipped)
                // Widening the instruction a skip steps over is not done here: it is very
                // probably fine on this part, but "very probably" does not belong in a code
                // generator and no test in this repository can currently reach the shape.
                // Today this is a gpasm Error[126], so naming it here is the same build
                // failure with a message that says where to look.
                throw new InvalidOperationException(
                    $"PIC18: the branch to '{line.Op1}' is {delta} words away, past BRA's range "
                    + $"of {BraMin}..{BraMax}, and it directly follows "
                    + $"'{lines[previousInstruction].Mnemonic}', which may skip it. Widening a "
                    + "skipped branch is not done here. Shorten the function, or split the "
                    + "branch by hand.");

            // Unmeasurable and skipped is left exactly as it was: that is today's behaviour,
            // and if the branch really is too far gpasm still says so. Turning an unknown into
            // a refusal would break programs that assemble today.
            if (!skipped && (outOfRange || !measurable))
            {
                line.Mnemonic = "GOTO";
                changed = true;
            }

            previousInstruction = i;
        }

        return changed;
    }
}
