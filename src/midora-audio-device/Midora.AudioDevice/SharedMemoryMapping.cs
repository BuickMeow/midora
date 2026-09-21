using System.IO.MemoryMappedFiles;

namespace Midora.AudioDevice;

/// <summary>
/// Backing store for the fixed-ABI shared blocks exchanged with the audio worker.
/// Named memory-mapped files are a Windows-only .NET feature, so every block is backed by a file
/// created inside the caller's owned exchange directory and mapped with a null map name. The
/// file-backed form is coherent across processes on Windows, macOS and Linux, and its lifecycle is
/// owned by the creator, which deletes the file when the block is disposed.
/// </summary>
public static class SharedMemoryMapping
{
    public static MemoryMappedFile Create(string path, long byteCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return MemoryMappedFile.CreateFromFile(
            path,
            FileMode.Create,
            mapName: null,
            byteCount,
            MemoryMappedFileAccess.ReadWrite);
    }

    public static MemoryMappedFile Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return MemoryMappedFile.CreateFromFile(
            path,
            FileMode.Open,
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.ReadWrite);
    }

    public static void TryDelete(string path)
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
