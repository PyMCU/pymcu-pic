using PyMCU.Backend.Targets.PIC18;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

// PIC18's operator switch at CompileBinary lists 13 of the 17 BinaryOp values. The four it
// does not list, Mul, Div, FloorDiv and Mod, never reach it: guards ABOVE it divert every one
// of them and each of those guards returns or throws. So the switch is exhaustive, and a
// `default:` in it could not fire. That was measured in pymcu-pic#8 and is why no `default:`
// was added.
//
// What that leaves is a switch whose exhaustiveness rests on the guards rather than on the
// shape of the enum. Delete one `return` and an operator falls into a switch with no case for
// it, which is pymcu-pic#6 on PIC12: a wrong value, emitted in silence. A `default:` would not
// protect against that, because nothing exercises a `default:` and it rots unseen. What
// protects it is asserting the instruction that comes out, per operator and per width.
//
// WHAT DISCRIMINATES, guard by guard, verified by deleting each `return` in turn:
//
//   8x8 into 16      the PRODH pair. Without the return the 16-bit path runs as well and the
//                    MULWF count goes from one to four.
//   16-bit Mul       the three partial products. Without the return the 8-bit path runs after
//                    it and overwrites the 16-bit result with a single low byte.
//   32-bit Mul       reaching the code at all. Without the return the next guard throws.
//   Div/FloorDiv/Mod the last instruction. Without the return an operand is reloaded into W
//                    after the result has already been stored, which no test that only looks
//                    for `CALL __udivN` can see. That is what NothingRunsAfterTheResultIsStored
//                    is for, and it is the one assertion here that covers every shape.
//
// WHAT IS INVARIANT, and here on purpose: FloorDiv and Mod issue the SAME call. They are told
// apart only by which scratch the answer is read back from, the quotient in __d_a or the
// remainder in __d_r. A test that asserted `CALL __udiv8` and stopped would pass just as
// happily if the backend returned one where the other belongs.
public class PIC18MulDivShapeTests
{
    private static readonly DeviceConfig Pic18f45k50 = new() { Chip = "pic18f45k50", Arch = "pic18" };

    private static string Compile(params Instruction[] body)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = "main", Body = body.ToList() });
        var sw = new StringWriter();
        new PIC18CodeGen(Pic18f45k50).Compile(prog, sw);
        return sw.ToString();
    }

    // The instructions of main, from its label to its RETURN. Everything the guards emit is in
    // here and nothing else is, so a fall-through shows up as extra entries.
    private static List<string> Body(string asm)
    {
        var body = new List<string>();
        bool inMain = false;
        foreach (var raw in asm.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line == "main:") { inMain = true; continue; }
            if (!inMain) continue;
            if (!line.StartsWith('\t')) break;
            string text = line.Trim();
            if (text == "RETURN") break;
            body.Add(text);
        }

        Assert.NotEmpty(body);
        return body;
    }

    private static string Product(DataType src, DataType dst) =>
        Compile(new Binary(BinaryOp.Mul, new Variable("a", src), new Variable("b", src),
                new Variable("c", dst)),
            new Return(new NoneVal()));

    private static string ProductByLiteral(DataType ty, int k) =>
        Compile(new Binary(BinaryOp.Mul, new Variable("a", ty), new Constant(k),
                new Variable("c", ty)),
            new Return(new NoneVal()));

    private static string Divide(BinaryOp op, DataType ty) =>
        Compile(new Binary(op, new Variable("a", ty), new Variable("b", ty),
                new Variable("c", ty)),
            new Return(new NoneVal()));

    private static int Count(IEnumerable<string> body, string mnemonic) =>
        body.Count(l => l.StartsWith(mnemonic + "\t"));

    // --- Mul, one case per shape the guards distinguish -------------------------

    [Fact]
    public void MulOfTwoBytesIntoAByte_KeepsTheLowHalfOfTheProduct()
    {
        var body = Body(Product(DataType.UINT8, DataType.UINT8));

        Assert.Equal(1, Count(body, "MULWF"));
        Assert.Contains("MULWF\tb, BANKED", body);
        Assert.Contains("MOVF\tPRODL, W, ACCESS", body);
        Assert.Contains("MOVWF\tc, BANKED", body);

        // An 8-bit destination takes PRODL and nothing else. PRODH here would mean the
        // 16-bit shape had been selected for a slot that cannot hold it.
        Assert.DoesNotContain(body, l => l.Contains("PRODH"));
    }

    [Fact]
    public void MulOfTwoBytesIntoSixteenBits_KeepsBothHalvesOfTheProduct()
    {
        var body = Body(Product(DataType.UINT8, DataType.UINT16));

        // One MULWF, because 8x8 into 16 is exactly what the hardware multiplier gives.
        Assert.Equal(1, Count(body, "MULWF"));
        Assert.Contains("MULWF\tb, BANKED", body);
        Assert.Contains("MOVF\tPRODL, W, ACCESS", body);
        Assert.Contains("MOVWF\tc, BANKED", body);
        Assert.Contains("MOVF\tPRODH, W, ACCESS", body);
        Assert.Contains("MOVWF\tc+1, BANKED", body);

        // The general 16-bit path accumulates through __mul_r. Seeing it here would mean this
        // shape had fallen into the one below.
        Assert.DoesNotContain(body, l => l.Contains("__mul_r"));
    }

    [Fact]
    public void MulOfTwoSixteenBitValues_SumsThreePartialProducts()
    {
        var body = Body(Product(DataType.UINT16, DataType.UINT16));

        // a.lo*b.lo, a.lo*b.hi, a.hi*b.lo. The fourth is entirely above the 16th bit.
        Assert.Equal(3, Count(body, "MULWF"));
        Assert.Contains("MOVFF\tPRODL, __mul_r", body);
        Assert.Contains("MOVFF\tPRODH, __mul_r+1", body);
        Assert.Contains("MOVWF\tc, BANKED", body);
        Assert.Contains("MOVWF\tc+1, BANKED", body);
    }

    [Fact]
    public void MulOfTwoThirtyTwoBitValues_ClearsTheAccumulatorAndSumsTenPartialProducts()
    {
        var body = Body(Product(DataType.UINT32, DataType.UINT32));

        Assert.Equal(10, Count(body, "MULWF"));
        Assert.Equal(4, Count(body, "CLRF"));       // the accumulator starts at zero
        Assert.Contains("CLRF\t__mul_r+3, ACCESS", body);
        for (int i = 0; i < 4; i++)
            Assert.Contains(i == 0 ? "MOVWF\tc, BANKED" : $"MOVWF\tc+{i}, BANKED", body);
    }

    [Fact]
    public void MulIntoAThreeByteSlot_IsRefusedByNameInsteadOfTruncating()
    {
        // No scalar type is three bytes wide, so this shape arrives only through a slot sized
        // by an array: three uint8 elements give the destination a width of 3. The guard for
        // it throws rather than falling through to the 8-bit path, which would store one byte
        // of the answer into a three-byte slot and say nothing.
        var ex = Assert.Throws<NotSupportedException>(() => Compile(
            new ArrayLoad("arr", new Constant(0), new Variable("t", DataType.UINT8),
                DataType.UINT8, 3),
            new Binary(BinaryOp.Mul, new Variable("a", DataType.UINT8),
                new Variable("b", DataType.UINT8), new Variable("arr", DataType.UINT8)),
            new Return(new NoneVal())));

        Assert.Contains("3-byte", ex.Message);
        Assert.Contains("truncated", ex.Message);
    }

    [Fact]
    public void MulOfAByteByALiteral_MultipliesTheOperandAndNotTheLiteralByItself()
    {
        var body = Body(ProductByLiteral(DataType.UINT8, 3));

        // The property, stated without naming an instruction: the operand reaches W and the
        // multiply happens next. Anything in between that writes W has thrown the operand
        // away, and the product cannot depend on it any more.
        int load = body.FindIndex(l => l == "MOVF\ta, W, BANKED");
        Assert.True(load >= 0, $"the operand is never loaded: {string.Join(" | ", body)}");
        Assert.StartsWith("MUL", body[load + 1]);

        // MULWF WREG squares W, so it is right only while W still holds the operand. That is
        // the shape this used to emit, one MOVLW too late.
        Assert.DoesNotContain("MULWF\tWREG, ACCESS", body);

        Assert.Contains("MOVWF\tc, BANKED", body);
    }

    [Theory]
    [InlineData(DataType.UINT16)]
    [InlineData(DataType.UINT32)]
    public void MulByALiteralAtTheWiderWidths_AlsoKeepsTheOperand(DataType ty)
    {
        // The wide paths stage the literal in a scratch and reload the operand afterwards.
        // They are here because the narrow path is the one that did not, and a fix that
        // touched the wrong shape would show up as one of these going quiet.
        var body = Body(ProductByLiteral(ty, 3));

        int load = body.FindIndex(l => l == "MOVF\ta, W, BANKED");
        Assert.True(load >= 0, $"the operand is never loaded: {string.Join(" | ", body)}");
        Assert.StartsWith("MUL", body[load + 1]);
    }

    // --- Division, at all three widths, told apart by where the answer is read --

    [Theory]
    [InlineData(DataType.UINT8, "__udiv8", 1)]
    [InlineData(DataType.UINT16, "__udiv16", 2)]
    [InlineData(DataType.UINT32, "__udiv32", 4)]
    public void FloorDivCallsTheRoutineForItsWidthAndReadsTheQuotient(
        DataType ty, string routine, int width)
    {
        var body = Body(Divide(BinaryOp.FloorDiv, ty));

        Assert.Contains($"CALL\t{routine}", body);
        for (int i = 0; i < width; i++)
        {
            Assert.Contains(i == 0 ? "MOVF\t__d_a, W, ACCESS" : $"MOVF\t__d_a+{i}, W, ACCESS", body);
            Assert.Contains(i == 0 ? "MOVWF\tc, BANKED" : $"MOVWF\tc+{i}, BANKED", body);
        }

        // The remainder is the other operator's answer.
        Assert.DoesNotContain(body, l => l.StartsWith("MOVF\t__d_r"));
    }

    [Theory]
    [InlineData(DataType.UINT8, "__udiv8", 1)]
    [InlineData(DataType.UINT16, "__udiv16", 2)]
    [InlineData(DataType.UINT32, "__udiv32", 4)]
    public void ModCallsTheSameRoutineButReadsTheRemainder(
        DataType ty, string routine, int width)
    {
        var body = Body(Divide(BinaryOp.Mod, ty));

        Assert.Contains($"CALL\t{routine}", body);
        for (int i = 0; i < width; i++)
        {
            Assert.Contains(i == 0 ? "MOVF\t__d_r, W, ACCESS" : $"MOVF\t__d_r+{i}, W, ACCESS", body);
            Assert.Contains(i == 0 ? "MOVWF\tc, BANKED" : $"MOVWF\tc+{i}, BANKED", body);
        }

        // The operands are STAGED into __d_a with MOVWF before the call; reading it back out
        // with MOVF afterwards is the quotient, which is `//` and not `%`.
        Assert.DoesNotContain(body, l => l.StartsWith("MOVF\t__d_a"));
    }

    // --- the guards returned ----------------------------------------------------

    // Every shape above, plus Div, which the front end lowers to floating point for integers
    // but which reaches CompileDivision when the IR asks for it directly.
    public static TheoryData<BinaryOp, DataType, DataType> EveryShape() => new()
    {
        { BinaryOp.Mul, DataType.UINT8, DataType.UINT8 },
        { BinaryOp.Mul, DataType.UINT8, DataType.UINT16 },
        { BinaryOp.Mul, DataType.UINT16, DataType.UINT16 },
        { BinaryOp.Mul, DataType.UINT32, DataType.UINT32 },
        { BinaryOp.FloorDiv, DataType.UINT8, DataType.UINT8 },
        { BinaryOp.FloorDiv, DataType.UINT16, DataType.UINT16 },
        { BinaryOp.FloorDiv, DataType.UINT32, DataType.UINT32 },
        { BinaryOp.Mod, DataType.UINT8, DataType.UINT8 },
        { BinaryOp.Mod, DataType.UINT16, DataType.UINT16 },
        { BinaryOp.Mod, DataType.UINT32, DataType.UINT32 },
        { BinaryOp.Div, DataType.UINT8, DataType.UINT8 },
        { BinaryOp.Div, DataType.UINT16, DataType.UINT16 },
    };

    // A literal on the right is its own branch inside the narrow Mul guard, with its own
    // `return`. Deleting that one is invisible to every shape above, because none of them
    // has a constant operand.
    public static TheoryData<DataType> EveryLiteralShape() => new()
    {
        DataType.UINT8, DataType.UINT16, DataType.UINT32,
    };

    [Theory]
    [MemberData(nameof(EveryLiteralShape))]
    public void NothingRunsAfterTheResultOfAMulByALiteralIsStored(DataType ty)
    {
        var body = Body(ProductByLiteral(ty, 3));
        Assert.StartsWith("MOVWF\tc", body[^1]);
    }

    [Theory]
    [MemberData(nameof(EveryShape))]
    public void NothingRunsAfterTheResultIsStored(BinaryOp op, DataType src, DataType dst)
    {
        // The guard for each of these operators must RETURN. If one of them stops returning,
        // the operator drops through to a switch with no case for it, and what that emits is
        // an operand reloaded into W after the answer has already been written: harmless
        // looking, invisible to any assertion that only searches for the operation, and one
        // edit away from being a wrong value instead.
        var body = Body(Compile(
            new Binary(op, new Variable("a", src), new Variable("b", src), new Variable("c", dst)),
            new Return(new NoneVal())));

        Assert.StartsWith("MOVWF\tc", body[^1]);
    }
}
