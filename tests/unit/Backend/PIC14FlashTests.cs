using PyMCU.Backend.Targets.PIC14;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Flash string / flash table lowering on the PIC14 backend: RETLW tables
/// reached through a computed GOTO (PCLATH:W -> PCL), FlashStrAddr
/// materialization, and argument passing into a flash-walking subroutine.
/// </summary>
public class PIC14FlashTests
{
    private static readonly DeviceConfig Pic16f877a =
        new() { Chip = "pic16f877a", TargetChip = "pic16f877a", Arch = "pic14", RamSize = 368 };

    private static string Compile(ProgramIR program)
    {
        var codegen = new PIC14CodeGen(Pic16f877a);
        var sw = new StringWriter();
        codegen.Compile(program, sw);
        return sw.ToString();
    }

    private static ProgramIR MakeProgram(params Instruction[] body)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = "main", Body = body.ToList() });
        return prog;
    }

    [Fact]
    public void ArrayLoadFlash_EmitsTableWithComputedGotoPrologue()
    {
        var prog = MakeProgram(
            new FlashData("__cstr_0", new List<int> { 0x68, 0x69, 0x00 }),
            new ArrayLoadFlash("__cstr_0", new Variable("main.i"), new Variable("main.b")));
        var asm = Compile(prog);

        // Caller: PCLATH = HIGH(entries), index in W, CALL, restore page 0.
        Assert.Contains("MOVLW\tHIGH(__flash___cstr_0_t)", asm);
        Assert.Contains("MOVWF\tPCLATH", asm);
        Assert.Contains("CALL\t__flash___cstr_0", asm);
        Assert.Contains("CLRF\tPCLATH", asm);

        // Table prologue: low byte + carry into PCLATH, then the computed jump.
        Assert.Contains("ADDLW\tLOW(__flash___cstr_0_t)", asm);
        Assert.Contains("INCF\tPCLATH, F", asm);
        Assert.Contains("MOVWF\tPCL", asm);

        // Entries survive as RETLW literals, table emitted once at the end.
        Assert.Contains("RETLW 0x68", asm);
        Assert.Contains("RETLW 0x69", asm);
        Assert.Contains("RETLW 0x00", asm);

        // Assembly-time 2K-page guard.
        Assert.Contains("IF ((__flash___cstr_0 >> 11) != ((__flash___cstr_0_t) >> 11))", asm);
        Assert.Contains("ERROR", asm);
    }

    [Fact]
    public void FlashLoadPtr_UsesSharedTrampoline_WithCarryIntoPclath()
    {
        var callee = new Function
        {
            Name = "f",
            Params = new List<string> { "f.s" },
            Body = new List<Instruction>
            {
                new FlashLoadPtr(new Variable("f.s", DataType.UINT16),
                    new Variable("f.i"), new Variable("f.b")),
                new Return(new NoneVal()),
            },
        };
        var prog = MakeProgram(
            new FlashData("__cstr_0", new List<int> { 0x41, 0x00 }),
            new Call("f", new List<Val> { new FlashStrAddr("__cstr_0") }, new NoneVal()));
        prog.Functions.Add(callee);
        var asm = Compile(prog);

        // Pointer high byte -> PCLATH, low byte + index in W with carry.
        Assert.Contains("MOVF\tf.s+1, W", asm);
        Assert.Contains("ADDWF\tf.s, W", asm);
        Assert.Contains("BTFSC\tSTATUS, 0", asm);
        Assert.Contains("INCF\tPCLATH, F", asm);
        Assert.Contains("CALL\t__flash_jmp", asm);

        // Shared trampoline emitted once, right before the tables.
        Assert.Contains("__flash_jmp:", asm);
        Assert.Contains("MOVWF\tPCL", asm);

        // Trampoline page guard covers first and last entry.
        Assert.Contains("IF ((__flash_jmp >> 11) != ((__flash___cstr_0_t) >> 11)) || ((__flash_jmp >> 11) != ((__flash___cstr_0_e - 1) >> 11))", asm);
    }

    [Fact]
    public void CallWithFlashStrAddr_CopiesEntriesAddressIntoParamSlot()
    {
        var callee = new Function
        {
            Name = "f",
            Params = new List<string> { "f.s" },
            Body = new List<Instruction>
            {
                new FlashLoadPtr(new Variable("f.s", DataType.UINT16),
                    new Constant(0), new Variable("f.b")),
                new Return(new NoneVal()),
            },
        };
        var prog = MakeProgram(
            new FlashData("__cstr_0", new List<int> { 0x41, 0x00 }),
            new Call("f", new List<Val> { new FlashStrAddr("__cstr_0") }, new NoneVal()));
        prog.Functions.Add(callee);
        var asm = Compile(prog);

        // 16-bit word address of the ENTRIES label lands in the param slot.
        Assert.Contains("MOVLW\tLOW(__flash___cstr_0_t)", asm);
        Assert.Contains("MOVWF\tf.s", asm);
        Assert.Contains("MOVLW\tHIGH(__flash___cstr_0_t)", asm);
        Assert.Contains("MOVWF\tf.s+1", asm);
        Assert.Contains("CALL\tf", asm);
    }

    [Fact]
    public void FlashData_Over256Bytes_Throws()
    {
        var prog = MakeProgram(
            new FlashData("big", Enumerable.Repeat(0x55, 257).ToList()),
            new ArrayLoadFlash("big", new Variable("main.i"), new Variable("main.b")));

        var ex = Assert.Throws<NotSupportedException>(() => Compile(prog));
        Assert.Contains("256", ex.Message);
    }
}
