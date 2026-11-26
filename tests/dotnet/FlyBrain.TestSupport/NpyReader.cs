using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace FlyBrain.TestSupport;

/// <summary>
/// Minimal reader for little-endian, C-order NumPy <c>.npy</c> files (format 1.0-3.0),
/// enough for tests to compare service output with derived data files.
/// </summary>
public static partial class NpyReader
{
    public sealed record NpyArray(string Descr, int[] Shape, byte[] Data)
    {
        public int Length => Shape.Aggregate(1, (product, size) => product * size);
    }

    public static NpyArray Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 10 || bytes[0] != 0x93 || Encoding.ASCII.GetString(bytes, 1, 5) != "NUMPY")
        {
            throw new InvalidDataException($"Not a .npy file: {path}");
        }

        int headerLength, headerStart;
        if (bytes[6] == 1)
        {
            headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
            headerStart = 10;
        }
        else
        {
            headerLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));
            headerStart = 12;
        }

        var header = Encoding.Latin1.GetString(bytes, headerStart, headerLength);
        var descr = DescrPattern().Match(header).Groups[1].Value;
        if (FortranPattern().Match(header).Groups[1].Value != "False")
        {
            throw new InvalidDataException($"Fortran-order arrays are not supported: {path}");
        }

        var shape = ShapePattern().Match(header).Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToArray();
        var dataStart = headerStart + headerLength;
        return new NpyArray(descr, shape, bytes[dataStart..]);
    }

    public static long[] ReadInt64(string path) => Convert(path, "<i8", 8, (span) => BinaryPrimitives.ReadInt64LittleEndian(span));

    public static float[] ReadFloat32(string path) => Convert(path, "<f4", 4, (span) => BinaryPrimitives.ReadSingleLittleEndian(span));

    public static bool[] ReadBool(string path) => Convert(path, "|b1", 1, (span) => span[0] != 0);

    private static T[] Convert<T>(string path, string expectedDescr, int itemSize, SpanReader<T> read)
    {
        var array = Read(path);
        if (array.Descr != expectedDescr)
        {
            throw new InvalidDataException($"Expected dtype {expectedDescr}, found {array.Descr}: {path}");
        }

        var result = new T[array.Length];
        if (array.Data.Length < result.Length * itemSize)
        {
            throw new InvalidDataException($"Truncated .npy data: {path}");
        }

        for (var index = 0; index < result.Length; index++)
        {
            result[index] = read(array.Data.AsSpan(index * itemSize, itemSize));
        }

        return result;
    }

    private delegate T SpanReader<out T>(ReadOnlySpan<byte> span);

    [GeneratedRegex(@"'descr':\s*'([^']+)'")]
    private static partial Regex DescrPattern();

    [GeneratedRegex(@"'fortran_order':\s*(True|False)")]
    private static partial Regex FortranPattern();

    [GeneratedRegex(@"'shape':\s*\(([^)]*)\)")]
    private static partial Regex ShapePattern();
}
