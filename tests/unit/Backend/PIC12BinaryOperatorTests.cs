using PyMCU.Backend.Targets.PIC12;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// pymcu-pic#6. PIC12 lowered 13 of the IR's 17 binary operators and the operator switch had no
/// default, which was worse than dropping the instruction: the operand load and the destination
/// store live OUTSIDE that switch, so an operator falling through wrote Src1 into Dst and left a
/// malformed instruction behind.
///
///     ; GPIO.value = a * b
///         MOVF   main.a, W
///         MOVWF  0x06          the wrong value, already stored
///         MOVF                 no operand
///
/// The build failed, but only because gpasm rejects that last line. An accident of the emitter,
/// not a guard: a fallthrough that emitted nothing, or emitted something syntactically valid,
/// would have been a silent wrong answer.
///
/// So the guard sits BEFORE the load rather than in the switch, and that is the property these
/// pin. The refusal by itself is not the point; refusing before anything is written is.
/// </summary>
public class PIC12BinaryOperatorTests
{
    private static readonly DeviceConfig Pic10f200 = new() { Chip = "pic10f200", Arch = "pic12" };

    private static string Compile(params Instruction[] body)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = "main", Body = body.ToList() });
        var sw = new StringWriter();
        new PIC12CodeGen(Pic10f200).Compile(prog, sw);
        return sw.ToString();
    }

    private static Instruction Op(BinaryOp op) =>
        new Binary(op, new Variable("a"), new Variable("b"), new Variable("d"));

    // One per operator, not one for the family.
    [Theory]
    [InlineData(BinaryOp.Mul)]
    [InlineData(BinaryOp.Div)]
    [InlineData(BinaryOp.FloorDiv)]
    [InlineData(BinaryOp.Mod)]
    public void AnOperatorWithNoLowering_IsRefusedAndNamed(BinaryOp op)
    {
        var message = Assert.ThrowsAny<Exception>(
            () => Compile(Op(op), new Return(new Constant(0)))).Message;

        Assert.Contains(op.ToString(), message);
        Assert.Contains("not implemented", message);
    }

    // The thirteen that work, each of them. This is what stops the guard from being too broad:
    // a supported operator refused would be a regression the four tests above cannot see.
    [Theory]
    [InlineData(BinaryOp.Add)]
    [InlineData(BinaryOp.Sub)]
    [InlineData(BinaryOp.BitAnd)]
    [InlineData(BinaryOp.BitOr)]
    [InlineData(BinaryOp.BitXor)]
    [InlineData(BinaryOp.LShift)]
    [InlineData(BinaryOp.RShift)]
    [InlineData(BinaryOp.Equal)]
    [InlineData(BinaryOp.NotEqual)]
    [InlineData(BinaryOp.LessThan)]
    [InlineData(BinaryOp.LessEqual)]
    [InlineData(BinaryOp.GreaterThan)]
    [InlineData(BinaryOp.GreaterEqual)]
    public void ASupportedOperator_StillLowers(BinaryOp op)
        => Assert.Contains("RETLW", Compile(Op(op), new Return(new Constant(0))));

    // The defect, stated as the thing that was wrong rather than as the exception. A supported
    // operator writes its destination; a refused one must not reach the point where it would.
    [Fact]
    public void ARefusedOperator_WritesNothingAtAll()
    {
        string supported = Compile(Op(BinaryOp.Add), new Return(new Constant(0)));
        Assert.Contains("MOVWF", supported);   // the store is there for one that works

        var sw = new StringWriter();
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction> { Op(BinaryOp.Mul), new Return(new Constant(0)) },
        });

        Assert.ThrowsAny<Exception>(() => new PIC12CodeGen(Pic10f200).Compile(prog, sw));
        Assert.DoesNotContain("MOVWF", sw.ToString());
    }
}
