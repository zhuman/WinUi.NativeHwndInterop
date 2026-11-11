using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;
using Windows.Win32.System.Threading;

namespace WinUI.NativeHwndInterop.Processes;

/// <summary>Owns an unnamed, non-inheritable kill-on-close Windows job. Does not impose UI restrictions.</summary>
public sealed class ProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;

    public unsafe ProcessJob()
    {
        _handle = PInvoke.CreateJobObject(null, null);
        if (_handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            _handle.Dispose();
            throw new Win32Exception(error, "Could not create the process job.");
        }
        HANDLE handle = new(_handle.DangerousGetHandle());
        try
        {
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = default;
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!PInvoke.SetInformationJobObject(handle, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                &limits, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not enable job kill-on-close.");
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    /// <summary>Assigns an existing process. For race-free containment, create it suspended and assign before resuming.</summary>
    /// <remarks>The process handle must allow PROCESS_SET_QUOTA and PROCESS_TERMINATE. Assignment failure is never ignored.</remarks>
    public void AssignProcess(SafeProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        bool jobRef = false, processRef = false;
        try
        {
            _handle.DangerousAddRef(ref jobRef);
            process.DangerousAddRef(ref processRef);
            if (!PInvoke.AssignProcessToJobObject(new HANDLE(_handle.DangerousGetHandle()), new HANDLE(process.DangerousGetHandle())))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not assign the process to its job.");
        }
        finally
        {
            if (processRef) process.DangerousRelease();
            if (jobRef) _handle.DangerousRelease();
        }
    }

    /// <summary>Requests termination of all job members, including descendants. Does not wait for exit.</summary>
    public void Terminate(uint exitCode = 1)
    {
        bool added = false;
        try
        {
            _handle.DangerousAddRef(ref added);
            if (!PInvoke.TerminateJobObject(new HANDLE(_handle.DangerousGetHandle()), exitCode))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not terminate the process job.");
        }
        finally { if (added) _handle.DangerousRelease(); }
    }

    /// <summary>Starts an executable suspended, assigns it to this job, then resumes it. Inherits the environment, not handles.</summary>
    /// <remarks>Does not use a shell, redirect streams, or change credentials. Custom launchers can instead call AssignProcess.</remarks>
    public unsafe Process StartProcess(string executablePath, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!Path.IsPathFullyQualified(executablePath)) throw new ArgumentException("An absolute executable path is required.", nameof(executablePath));
        var command = new StringBuilder(Quote(executablePath));
        foreach (string argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            command.Append(' ').Append(Quote(argument));
        }
        char[] commandLine = (command.ToString() + '\0').ToCharArray();
        STARTUPINFOW startup = new() { cb = (uint)sizeof(STARTUPINFOW) };
        PROCESS_INFORMATION info;
        fixed (char* executable = executablePath)
        fixed (char* line = commandLine)
        fixed (char* directory = workingDirectory)
        {
            if (!PInvoke.CreateProcess(executable, line, null, null, false, PROCESS_CREATION_FLAGS.CREATE_SUSPENDED,
                null, directory, &startup, &info))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the suspended process.");
        }
        Process? process = null;
        try
        {
            process = Process.GetProcessById((int)info.dwProcessId);
            AssignProcess(process.SafeHandle);
            if (PInvoke.ResumeThread(info.hThread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not resume the assigned process.");
            return process;
        }
        catch (Exception launchError)
        {
            process?.Dispose();
            if (!PInvoke.TerminateProcess(info.hProcess, 1))
                throw new AggregateException("Launch and suspended-process cleanup failed.", launchError,
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            throw;
        }
        finally
        {
            PInvoke.CloseHandle(info.hThread);
            PInvoke.CloseHandle(info.hProcess);
        }
    }

    private static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("Process arguments cannot contain NUL.", nameof(value));
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    /// <summary>Closes this job handle; Windows terminates remaining members when the last job handle closes.</summary>
    public void Dispose() => _handle.Dispose();
}
