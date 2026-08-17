using System.Text;

namespace Midora.Audio.Bass;

internal readonly record struct PersistentAudioWorkerResponse(
    bool Succeeded,
    int ActualSampleRate,
    int ActualDeviceBufferFrameCount,
    string Error);

internal static class PersistentAudioWorkerExchange
{
    private const int Magic = 0x3157414d;
    private const int Version = 1;
    private const int MaximumArgumentCount = 64;
    private const int MaximumStringByteCount = 1024 * 1024;

    public static void WriteRequest(string directory, long generation, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (generation <= 0 || arguments.Count > MaximumArgumentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }
        WriteAtomically(RequestPath(directory, generation), writer =>
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(arguments.Count);
            foreach (string argument in arguments)
            {
                WriteString(writer, argument);
            }
        });
    }

    public static string[] ReadRequest(string directory, long generation)
    {
        string path = RequestPath(directory, generation);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
        {
            throw new InvalidDataException("The persistent audio worker request header is invalid.");
        }
        int count = reader.ReadInt32();
        if (count is < 0 or > MaximumArgumentCount)
        {
            throw new InvalidDataException("The persistent audio worker request count is invalid.");
        }
        string[] result = new string[count];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = ReadString(reader);
        }
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("The persistent audio worker request has trailing data.");
        }
        return result;
    }

    public static void WriteResponse(
        string directory,
        long generation,
        PersistentAudioWorkerResponse response) =>
        WriteAtomically(ResponsePath(directory, generation), writer =>
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(response.Succeeded);
            writer.Write(response.ActualSampleRate);
            writer.Write(response.ActualDeviceBufferFrameCount);
            WriteString(writer, response.Error ?? string.Empty);
        });

    public static bool TryReadResponse(
        string directory,
        long generation,
        out PersistentAudioWorkerResponse response)
    {
        string path = ResponsePath(directory, generation);
        if (!File.Exists(path))
        {
            response = default;
            return false;
        }
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
        {
            throw new InvalidDataException("The persistent audio worker response header is invalid.");
        }
        response = new(
            reader.ReadBoolean(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            ReadString(reader));
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("The persistent audio worker response has trailing data.");
        }
        return true;
    }

    public static void DeleteExchange(string directory, long generation)
    {
        TryDelete(RequestPath(directory, generation));
        TryDelete(ResponsePath(directory, generation));
    }

    private static string RequestPath(string directory, long generation) =>
        Path.Combine(RequireDirectory(directory), $"request-{generation}.mawr");

    private static string ResponsePath(string directory, long generation) =>
        Path.Combine(RequireDirectory(directory), $"response-{generation}.maws");

    private static string RequireDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Path.GetFullPath(directory);
    }

    private static void WriteAtomically(string path, Action<BinaryWriter> write)
    {
        string temporaryPath = path + ".tmp";
        TryDelete(temporaryPath);
        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.WriteThrough))
            using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
            {
                write(writer);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaximumStringByteCount)
        {
            throw new InvalidDataException("The persistent audio worker string is too long.");
        }
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length is < 0 or > MaximumStringByteCount)
        {
            throw new InvalidDataException("The persistent audio worker string length is invalid.");
        }
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException(
                "The persistent audio worker string payload is truncated.");
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
