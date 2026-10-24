namespace Atsm.Protocol.Lis01;

public static class ControlCharacters
{
    public const byte Soh = 0x01;
    public const byte Stx = 0x02;
    public const byte Etx = 0x03;
    public const byte Eot = 0x04;
    public const byte Enq = 0x05;
    public const byte Ack = 0x06;
    public const byte Lf = 0x0A;
    public const byte Cr = 0x0D;
    public const byte Dle = 0x10;
    public const byte Nak = 0x15;
    public const byte Syn = 0x16;
    public const byte Etb = 0x17;

    /// <summary>Characters LIS01-A2 forbids inside frame text (CR is allowed as record terminator).</summary>
    public static bool IsRestricted(byte value) => value switch
    {
        Soh or Stx or Etx or Eot or Enq or Ack or Dle or Nak or Syn or Etb or Lf => true,
        >= 0x11 and <= 0x14 => true,
        _ => false,
    };
}
