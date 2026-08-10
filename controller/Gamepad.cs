using System.Runtime.InteropServices;

namespace HeavierByTheKill.Controller;

internal static class GamepadInput
{
    const ushort RightShoulder=0x0200;

    [StructLayout(LayoutKind.Sequential)]
    struct State
    {
        public uint PacketNumber;
        public Pad Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Pad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [DllImport("xinput1_4.dll",EntryPoint="XInputGetState")]
    static extern uint GetState(uint userIndex,out State state);

    internal static bool MenuActionButtonDown()
    {
        try
        {
            for(uint index=0;index<4;index++)
            {
                if(GetState(index,out var state)==0
                    && (state.Pad.Buttons&RightShoulder)!=0)
                    return true;
            }
        }
        catch(DllNotFoundException) { }
        catch(EntryPointNotFoundException) { }
        return false;
    }

    internal static ushort Buttons()
    {
        try
        {
            for(uint index=0;index<4;index++)
                if(GetState(index,out var state)==0) return state.Pad.Buttons;
        }
        catch(DllNotFoundException) { }
        catch(EntryPointNotFoundException) { }
        return 0;
    }

}
