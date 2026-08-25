using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Midora.Audio.Bass;

public readonly record struct AudioWorkerResourceSnapshot(
    int ActiveProcessCount,
    TimeSpan TotalProcessorTime,
    long WorkingSetBytes,
    long PrivateMemoryBytes);

/// <summary>
/// Owns the Windows Job Object used by every audio worker started by this process and exposes
/// read-only aggregate resource counters for user-facing diagnostics.
/// </summary>
public static partial class AudioWorkerProcessGroup
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectBasicAccountingInformationClass = 1;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private static readonly object Gate = new();
    private static readonly Dictionary<int, WorkerRegistration> Registrations = [];
    private static SafeFileHandle? _jobHandle;
    private static bool _jobInitializationAttempted;
    private static bool _shuttingDown;

    public static Process Start(ProcessStartInfo startInfo, string failureMessage)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(failureMessage);
        Register(process);
        return process;
    }

    public static AudioWorkerResourceSnapshot CaptureResources()
    {
        WorkerRegistration[] registrations;
        bool hasJobAccounting;
        TimeSpan jobProcessorTime;
        lock (Gate)
        {
            registrations = Registrations.Values.ToArray();
            hasJobAccounting = TryReadJobProcessorTimeLocked(out jobProcessorTime);
        }

        int activeProcessCount = 0;
        long workingSetBytes = 0;
        long privateMemoryBytes = 0;
        long unassignedProcessorTicks = 0;
        foreach (WorkerRegistration registration in registrations)
        {
            try
            {
                using Process process = Process.GetProcessById(registration.ProcessId);
                if (process.HasExited || !MatchesStartTime(process, registration.StartTimeUtcTicks))
                {
                    Remove(registration);
                    continue;
                }

                process.Refresh();
                activeProcessCount++;
                workingSetBytes = SaturatingAdd(workingSetBytes, process.WorkingSet64);
                privateMemoryBytes = SaturatingAdd(privateMemoryBytes, process.PrivateMemorySize64);
                if (!hasJobAccounting || !registration.AssignedToJob)
                {
                    unassignedProcessorTicks = SaturatingAdd(
                        unassignedProcessorTicks,
                        process.TotalProcessorTime.Ticks);
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or NotSupportedException)
            {
                Remove(registration);
            }
        }

        long processorTicks = hasJobAccounting
            ? SaturatingAdd(jobProcessorTime.Ticks, unassignedProcessorTicks)
            : unassignedProcessorTicks;
        return new(
            activeProcessCount,
            TimeSpan.FromTicks(processorTicks),
            workingSetBytes,
            privateMemoryBytes);
    }

    internal static bool IsTracked(int processId)
    {
        lock (Gate)
        {
            return Registrations.ContainsKey(processId);
        }
    }

    public static void Shutdown()
    {
        WorkerRegistration[] unassigned;
        SafeFileHandle? jobHandle;
        lock (Gate)
        {
            if (_shuttingDown)
            {
                return;
            }

            _shuttingDown = true;
            unassigned = Registrations.Values.Where(value => !value.AssignedToJob).ToArray();
            Registrations.Clear();
            jobHandle = _jobHandle;
            _jobHandle = null;
        }

        // Closing the Job handle is the authoritative termination path for assigned workers.
        jobHandle?.Dispose();
        foreach (WorkerRegistration registration in unassigned)
        {
            TryTerminateFallbackWorker(registration);
        }
    }

    private static void Register(Process process)
    {
        int processId;
        long startTimeUtcTicks;
        try
        {
            processId = process.Id;
            startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return;
        }

        bool assignedToJob;
        lock (Gate)
        {
            if (_shuttingDown)
            {
                assignedToJob = false;
            }
            else
            {
                assignedToJob = TryAssignToJobLocked(process);
                Registrations[processId] = new(processId, startTimeUtcTicks, assignedToJob);
            }
        }

        if (_shuttingDown)
        {
            TryTerminateFallbackWorker(new(processId, startTimeUtcTicks, assignedToJob));
            return;
        }

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Remove(new(processId, startTimeUtcTicks, assignedToJob));
            if (process.HasExited)
            {
                Remove(new(processId, startTimeUtcTicks, assignedToJob));
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or ObjectDisposedException)
        {
            if (process.HasExited)
            {
                Remove(new(processId, startTimeUtcTicks, assignedToJob));
            }
        }
    }

    private static bool TryAssignToJobLocked(Process process)
    {
        SafeFileHandle? handle = GetOrCreateJobLocked();
        if (handle is null || handle.IsInvalid || handle.IsClosed)
        {
            return false;
        }

        try
        {
            return AssignProcessToJobObject(handle, process.SafeHandle) != 0;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or ObjectDisposedException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static SafeFileHandle? GetOrCreateJobLocked()
    {
        if (!OperatingSystem.IsWindows() || _jobInitializationAttempted)
        {
            return _jobHandle;
        }

        _jobInitializationAttempted = true;
        SafeFileHandle handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        JobObjectExtendedLimitInformation limits = new();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformationClass,
                ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()) == 0)
        {
            handle.Dispose();
            return null;
        }

        _jobHandle = handle;
        return handle;
    }

    private static bool TryReadJobProcessorTimeLocked(out TimeSpan processorTime)
    {
        processorTime = TimeSpan.Zero;
        SafeFileHandle? handle = _jobHandle;
        if (handle is null || handle.IsInvalid || handle.IsClosed)
        {
            return false;
        }

        if (QueryInformationJobObject(
                handle,
                JobObjectBasicAccountingInformationClass,
                out JobObjectBasicAccountingInformation accounting,
                (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(),
                IntPtr.Zero) == 0)
        {
            return false;
        }

        processorTime = TimeSpan.FromTicks(SaturatingAdd(
            accounting.TotalUserTime,
            accounting.TotalKernelTime));
        return true;
    }

    private static void Remove(WorkerRegistration registration)
    {
        lock (Gate)
        {
            if (Registrations.TryGetValue(registration.ProcessId, out WorkerRegistration current)
                && current.StartTimeUtcTicks == registration.StartTimeUtcTicks)
            {
                Registrations.Remove(registration.ProcessId);
            }
        }
    }

    private static void TryTerminateFallbackWorker(WorkerRegistration registration)
    {
        try
        {
            using Process process = Process.GetProcessById(registration.ProcessId);
            if (!process.HasExited && MatchesStartTime(process, registration.StartTimeUtcTicks))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
        }
    }

    private static bool MatchesStartTime(Process process, long expectedUtcTicks)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks == expectedUtcTicks;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right)
        {
            return long.MaxValue;
        }
        if (right < 0 && left < long.MinValue - right)
        {
            return long.MinValue;
        }
        return left + right;
    }

    private readonly record struct WorkerRegistration(
        int ProcessId,
        long StartTimeUtcTicks,
        bool AssignedToJob);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int AssignProcessToJobObject(
        SafeFileHandle job,
        SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int QueryInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        out JobObjectBasicAccountingInformation information,
        uint informationLength,
        IntPtr returnLength);
}
