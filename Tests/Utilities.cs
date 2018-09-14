using System;
using System.Runtime.CompilerServices;

namespace Tests
{
    internal static class Verify
    {
        public static void That(bool condition, string message = null)
        {
            if (!condition)
            {
                throw string.IsNullOrEmpty(message)
                        ? new InvalidOperationException()
                        : new InvalidOperationException(message);
            }
        }
    }

    internal static class Cast
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static short ToShort(int value)
        {
            Verify.That(short.MinValue <= value && value <= short.MaxValue);
            return (short)value;
        }
    }
}
