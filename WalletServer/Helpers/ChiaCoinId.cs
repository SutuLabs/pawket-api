using System.Numerics;
using System.Security.Cryptography;
using chia.dotnet;

namespace WalletServer.Helpers;

/// <summary>Chia coin names use signed minimal big-endian CLVM integer bytes for the amount.</summary>
internal static class ChiaCoinId
{
    public static string FromCoin(Coin coin)
    {
        var parent = Convert.FromHexString(coin.ParentCoinInfo.Unprefix0x());
        var puzzle = Convert.FromHexString(coin.PuzzleHash.Unprefix0x());
        var amount = coin.Amount == 0
            ? Array.Empty<byte>()
            : new BigInteger(coin.Amount).ToByteArray(isUnsigned: false, isBigEndian: true);
        var bytes = new byte[parent.Length + puzzle.Length + amount.Length];
        parent.CopyTo(bytes, 0);
        puzzle.CopyTo(bytes, parent.Length);
        amount.CopyTo(bytes, parent.Length + puzzle.Length);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
