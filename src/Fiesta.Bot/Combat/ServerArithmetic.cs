namespace Fiesta.Bot.Combat;

/// <summary>The primitive operations the server's damage code is built from, reproduced exactly.
///
/// <para>⚠️ <b>These are not general-purpose maths helpers and must not be "tidied".</b> Every one of them
/// exists because the obvious C# equivalent gives a different answer from the server. They are verified by
/// differential fuzzing against the real <c>Zone.exe</c> code running under emulation
/// (<c>tools/fuzz_extreme.py</c>), at exact bitwise equality — so an "equivalent" rewrite that shifts a
/// rounding boundary is a regression the tests will catch but the compiler will not.</para>
///
/// See docs/DAMAGE_FORMULA.md.</summary>
internal static class ServerArithmetic
{
    /// <summary>Four permille multipliers applied before a single divide, hence 1000^4. The literal in the
    /// binary is 1e12 (the constant at 0x6CFE28).</summary>
    internal const double RateDivisor = 1_000_000_000_000.0;

    /// <summary>One permille multiplier: <c>value * rate / 1000</c>, multiply then divide, in that order.
    ///
    /// The order is load-bearing where the result is later truncated: taking the product of several rates
    /// first and dividing once at the end is the same value in exact arithmetic but rounds differently, and
    /// the accessors that truncate turn that into a whole point of armour.</summary>
    internal static double ApplyRate(double value, int ratePermille) => value * ratePermille / 1000.0;

    /// <summary>The floor every accessor applies to its result: anything below 1 becomes 1.
    ///
    /// <para><b>The threshold is <c>&lt; 1</c>, not <c>&lt;= 0</c>.</b> Measured: feed a core chain of 0.5
    /// and an own half of 10 and every accessor returns 11.0, so the fractional 0.5 was raised to 1 rather
    /// than left alone. <c>roe_TH</c> settles it because it does not truncate its sum, so 11.0 cannot be
    /// a rounded 10.5.</para></summary>
    internal static double FloorAtOne(double value) => value < 1.0 ? 1.0 : value;

    /// <summary>The C runtime's <c>_ftol</c>: truncate toward zero into a 64-bit integer, then keep the
    /// <b>low 32 bits</b> as a signed int. A conversion that WRAPS, not one that saturates.
    ///
    /// <para>This is why searching the accessors for <c>fistp</c> finds nothing and suggests there is no
    /// integer conversion at all — the conversion is a call into the CRT helper.</para>
    ///
    /// <para>Proven against the server, not assumed: a summed value of 4294967302 comes back as
    /// <b>6.0</b>, 4294967395 as <b>99.0</b>, and -2148557388.824 as <b>2146409908.0</b> — in each case
    /// exactly the low 32 bits. <see cref="System.Math.Truncate(double)"/> keeps the full magnitude and
    /// disagrees with all three.</para>
    ///
    /// <para>Beyond int64 range the x87 stores the 64-bit integer indefinite (0x8000000000000000), whose
    /// low dword is zero.</para></summary>
    internal static double Ftol32(double value)
    {
        if (double.IsNaN(value) || value >= 9.2233720368547758E18 || value <= -9.2233720368547758E18)
            return 0.0;
        return unchecked((int)(long)value);
    }
}
