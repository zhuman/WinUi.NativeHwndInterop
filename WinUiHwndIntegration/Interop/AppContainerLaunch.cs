using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Security.Authorization;
using Windows.Win32.System.Threading;
using WinUI.NativeHwndInterop.Processes;

namespace WinUiHwndIntegration.Interop;

/// <summary>Per-session AppContainer experiment with no requested capabilities. Never changes repository or runtime ACLs.</summary>
internal sealed unsafe class AppContainerLaunch : IDisposable
{
    private readonly string _name = $"WinUiHwndPlugin.{Guid.NewGuid():N}";
    private PSID _sid;
    private string? _payload;
    private bool _profileCreated;
    private bool _disposed;

    public SecurityIdentifier PackageSid { get; private set; } = null!;
    public string ExecutablePath => Path.Combine(_payload ?? throw new InvalidOperationException("Payload not staged."), "WinUiHwndIntegration.Plugin.exe");

    public AppContainerLaunch(string sourceDirectory)
    {
        try
        {
            PSID sid;
            fixed (char* name = _name)
            fixed (char* display = "WinUI HWND plugin experiment")
            fixed (char* description = "Temporary plugin profile with no requested capabilities")
                PInvoke.CreateAppContainerProfile(name, display, description, null, 0, &sid).ThrowOnFailure();
            _sid = sid;
            _profileCreated = true;
            PackageSid = new SecurityIdentifier((nint)sid.Value);
            PInvoke.GetAppContainerFolderPath(PackageSid.Value, out PWSTR folder).ThrowOnFailure();
            string profilePath;
            try { profilePath = folder.ToString(); }
            finally { Marshal.FreeCoTaskMem((nint)folder.Value); }
            _payload = Path.Combine(profilePath, "PluginPayload");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            using var identity = WindowsIdentity.GetCurrent();
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(PackageSid, FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(_payload).Create(security);
            foreach (string file in Directory.GetFiles(sourceDirectory))
                File.Copy(file, Path.Combine(_payload, Path.GetFileName(file)));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public NamedPipeServerStream CreatePipe(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetSecurityDescriptorSddlForm($"D:P(A;;GA;;;{identity.User!.Value})(A;;GA;;;SY)(A;;GRGW;;;{PackageSid.Value})");
        var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security,
            HandleInheritability.None, PipeAccessRights.TakeOwnership);
        try
        {
            // Set only the mandatory label, not an audit SACL (which would require SeSecurityPrivilege).
            PSECURITY_DESCRIPTOR descriptor;
            if (!PInvoke.ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;;NW;;;LW)", 1, out descriptor, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not construct the pipe's low integrity label.");
            try
            {
                BOOL present, defaulted;
                ACL* sacl;
                if (!PInvoke.GetSecurityDescriptorSacl(descriptor, &present, &sacl, &defaulted))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the pipe mandatory label.");
                uint error = (uint)PInvoke.SetSecurityInfo(new HANDLE(pipe.SafePipeHandle.DangerousGetHandle()), SE_OBJECT_TYPE.SE_KERNEL_OBJECT,
                    OBJECT_SECURITY_INFORMATION.LABEL_SECURITY_INFORMATION, default, default, null, sacl);
                if (error != 0) throw new Win32Exception((int)error, "Could not set the pipe's low integrity label.");
            }
            finally { PInvoke.LocalFree(new HLOCAL((nint)descriptor.Value)); }
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    public Process Start(IReadOnlyList<string> arguments, ProcessJob job)
    {
        nuint length = 0;
        PInvoke.InitializeProcThreadAttributeList(default, 1, 0, &length);
        if (Marshal.GetLastPInvokeError() != 122) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not size the startup attribute list.");
        var attributes = (LPPROC_THREAD_ATTRIBUTE_LIST)NativeMemory.Alloc(length);
        bool initialized = false;
        try
        {
            if (!PInvoke.InitializeProcThreadAttributeList(attributes, 1, 0, &length))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not initialize startup attributes.");
            initialized = true;
            SECURITY_CAPABILITIES capabilities = new() { AppContainerSid = _sid };
            if (!PInvoke.UpdateProcThreadAttribute(attributes, 0, (nuint)0x20009, &capabilities, (nuint)sizeof(SECURITY_CAPABILITIES), null, null))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not set AppContainer security capabilities.");
            STARTUPINFOEXW startup = new() { lpAttributeList = attributes };
            startup.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
            string command = $"\"{ExecutablePath}\" " + string.Join(" ", arguments.Select(QuoteArgument));
            char[] mutableCommand = (command + '\0').ToCharArray();
            PROCESS_INFORMATION info;
            fixed (char* executable = ExecutablePath)
            fixed (char* commandLine = mutableCommand)
            fixed (char* directory = _payload)
            {
                if (!PInvoke.CreateProcess(executable, commandLine, null, null, false,
                    PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT | PROCESS_CREATION_FLAGS.CREATE_SUSPENDED, null, directory, (STARTUPINFOW*)&startup, &info))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcess(AppContainer) failed; no unrestricted fallback was launched.");
            }
            Process? process = null;
            try
            {
                process = Process.GetProcessById((int)info.dwProcessId);
                job.AssignProcess(process.SafeHandle);
                if (PInvoke.ResumeThread(info.hThread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not resume the job-assigned AppContainer process.");
                return process;
            }
            catch (Exception launchError)
            {
                process?.Dispose();
                if (!PInvoke.TerminateProcess(info.hProcess, 1))
                    throw new AggregateException("AppContainer launch and suspended-process cleanup failed.", launchError,
                        new Win32Exception(Marshal.GetLastPInvokeError()));
                throw;
            }
            finally
            {
                PInvoke.CloseHandle(info.hThread);
                PInvoke.CloseHandle(info.hProcess);
            }
        }
        finally
        {
            if (initialized) PInvoke.DeleteProcThreadAttributeList(attributes);
            NativeMemory.Free(attributes.Value);
        }
    }

    private static string QuoteArgument(string value)
    {
        // Protocol launch arguments contain no paths and never contain quotes or whitespace.
        if (value.Any(char.IsWhiteSpace) || value.Contains('"')) throw new ArgumentException("Invalid plugin launch argument.");
        return value;
    }

    public static string VerifyToken(Process process, SecurityIdentifier? expectedPackage)
    {
        HANDLE token;
        if (!PInvoke.OpenProcessToken(new HANDLE(process.Handle), TOKEN_ACCESS_MASK.TOKEN_QUERY, &token))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not query the plugin token.");
        try
        {
            uint isContainer;
            uint returned;
            if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIsAppContainer, &isContainer, sizeof(uint), &returned))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not query TokenIsAppContainer.");
            if ((isContainer != 0) != (expectedPackage is not null)) throw new InvalidOperationException("Plugin token does not match the requested isolation mode.");
            if (expectedPackage is not null)
            {
                PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenAppContainerSid, null, 0, &returned);
                byte[] packageData = new byte[returned];
                fixed (byte* buffer = packageData)
                {
                    if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenAppContainerSid, buffer, returned, &returned))
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not query the plugin package SID.");
                    if (new SecurityIdentifier((nint)((TOKEN_APPCONTAINER_INFORMATION*)buffer)->TokenAppContainer.Value) != expectedPackage)
                        throw new InvalidOperationException("Plugin has an unexpected AppContainer SID.");
                }
            }
            PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenCapabilities, null, 0, &returned);
            byte[] capabilityData = new byte[returned];
            uint capabilityCount;
            fixed (byte* buffer = capabilityData)
            {
                if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenCapabilities, buffer, returned, &returned))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not query the plugin capability SIDs.");
                capabilityCount = ((TOKEN_GROUPS*)buffer)->GroupCount;
            }
            PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, null, 0, &returned);
            byte[] data = new byte[returned];
            fixed (byte* buffer = data)
            {
                if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer, returned, &returned))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not query the plugin integrity level.");
                string sid = new SecurityIdentifier((nint)((TOKEN_MANDATORY_LABEL*)buffer)->Label.Sid.Value).Value;
                if (expectedPackage is not null && sid != "S-1-16-4096") throw new InvalidOperationException($"Expected low integrity, got {sid}.");
                return $"AppContainer={isContainer != 0}, integrity={sid}, capability SIDs={capabilityCount}";
            }
        }
        finally { PInvoke.CloseHandle(token); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_payload is not null && Directory.Exists(_payload))
            {
                foreach (string file in Directory.GetFiles(_payload))
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        try { File.Delete(file); break; }
                        catch (Exception error) when (attempt < 20 &&
                            (error is UnauthorizedAccessException || error is IOException && (error.HResult & 0xFFFF) == 32))
                        {
                            // Process exit can precede release of the executable's image mapping.
                            Thread.Sleep(100);
                        }
                    }
                }
                Directory.Delete(_payload);
            }
            if (_profileCreated) PInvoke.DeleteAppContainerProfile(_name).ThrowOnFailure();
        }
        finally
        {
            if (!_sid.IsNull) PInvoke.FreeSid(_sid);
        }
    }
}
