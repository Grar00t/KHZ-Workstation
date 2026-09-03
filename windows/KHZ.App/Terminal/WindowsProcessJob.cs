using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KHZ.App.Terminal;

internal sealed class WindowsProcessJob
    : IDisposable
{
    private const uint JobObjectLimitDieOnUnhandledException =
        0x00000400;

    private const uint JobObjectLimitKillOnJobClose =
        0x00002000;

    private readonly SafeFileHandle _handle;

    private WindowsProcessJob(
        SafeFileHandle handle)
    {
        _handle = handle;
    }

    internal static WindowsProcessJob Attach(
        Process process)
    {
        ArgumentNullException.ThrowIfNull(
            process);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows Job Objects are available only on Windows.");
        }

        var handle =
            CreateJobObject(
                IntPtr.Zero,
                null);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows Job Object creation failed.");
        }

        try
        {
            var limits =
                new JobObjectExtendedLimitInformation
                {
                    BasicLimitInformation =
                        new JobObjectBasicLimitInformation
                        {
                            LimitFlags =
                                JobObjectLimitDieOnUnhandledException
                                | JobObjectLimitKillOnJobClose
                        }
                };

            var length =
                Marshal.SizeOf<JobObjectExtendedLimitInformation>();

            var buffer =
                Marshal.AllocHGlobal(
                    length);

            try
            {
                Marshal.StructureToPtr(
                    limits,
                    buffer,
                    fDeleteOld: false);

                if (!SetInformationJobObject(
                        handle,
                        JobObjectInformationClass.ExtendedLimitInformation,
                        buffer,
                        (uint)length))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows Job Object limits could not be applied.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(
                    buffer);
            }

            if (!AssignProcessToJobObject(
                    handle,
                    process.Handle))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "PowerShell could not be assigned to the KHZ Job Object.");
            }

            return new WindowsProcessJob(
                handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
        => _handle.Dispose();

    private enum JobObjectInformationClass
    {
        ExtendedLimitInformation = 9
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
        public IntPtr Affinity;
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

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(
        IntPtr jobAttributes,
        string? name);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        JobObjectInformationClass informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        IntPtr process);
}
