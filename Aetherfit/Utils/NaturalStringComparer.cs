using System.Collections.Generic;

namespace Aetherfit.Utils;

// Orders strings so embedded numbers sort by value rather than lexically ("Outfit 2" before "Outfit 10").
// Case-insensitive (ordinal upper-invariant). Shared by the folder tree, the local gallery, and the shared gallery.
internal sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer OrdinalIgnoreCase = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            var cx = x[i];
            var cy = y[j];

            if (char.IsDigit(cx) && char.IsDigit(cy))
            {
                var xStart = i;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                var yStart = j;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                var xDigit = xStart;
                while (xDigit < i - 1 && x[xDigit] == '0') xDigit++;
                var yDigit = yStart;
                while (yDigit < j - 1 && y[yDigit] == '0') yDigit++;

                var xLen = i - xDigit;
                var yLen = j - yDigit;

                if (xLen != yLen) return xLen - yLen;
                for (var k = 0; k < xLen; k++)
                {
                    var d = x[xDigit + k] - y[yDigit + k];
                    if (d != 0) return d;
                }

                var leadX = xDigit - xStart;
                var leadY = yDigit - yStart;
                if (leadX != leadY) return leadX - leadY;
            }
            else
            {
                var ux = char.ToUpperInvariant(cx);
                var uy = char.ToUpperInvariant(cy);
                if (ux != uy) return ux - uy;
                i++;
                j++;
            }
        }

        return (x.Length - i) - (y.Length - j);
    }
}
