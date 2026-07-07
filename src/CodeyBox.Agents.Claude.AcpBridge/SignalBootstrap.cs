using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CodeyBox.Agents.Claude.AcpBridge;

internal static class SignalBootstrap
{
    private const int SigHup = 1;
    private const int SigInt = 2;
    private const int SigTerm = 15;
    
    internal const string SignalBootstrapReexecEnv = "CODEYBOX_ACPBRIDGE_SIGNAL_BOOTSTRAP_REEXECED";

    internal static void ResetInheritedIgnoredSignalToDefault(int signalNumber)
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Detached launchers can inherit SIG_IGN for SIGHUP/SIGINT/SIGTERM.
        // PosixSignalRegistration honours inherited ignores and will not make
        // such a signal catchable, so reset only that disposition before
        // registering the bridge's shutdown handler.
        if (!NativeMethods.TryReadSignalHandler(signalNumber, out var handler))
        {
            throw new InvalidOperationException($"Failed to read signal handler for signal {signalNumber} prior to registration.");
        }

        if (handler != NativeMethods.SigIgn)
        {
            return;
        }

        IntPtr prev;
        try
        {
            prev = NativeMethods.Signal(signalNumber, NativeMethods.SigDfl);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to invoke signal P/Invoke for signal {signalNumber}.", ex);
        }

        if (prev == new IntPtr(-1))
        {
            throw new InvalidOperationException($"signal(2) failed to reset signal {signalNumber} disposition to default. Marshal error code: {Marshal.GetLastWin32Error()}");
        }

        // Verify it was actually reset
        if (!NativeMethods.TryReadSignalHandler(signalNumber, out var afterHandler))
        {
            throw new InvalidOperationException($"Failed to verify signal {signalNumber} disposition after resetting.");
        }

        if (afterHandler == NativeMethods.SigIgn)
        {
            throw new InvalidOperationException($"Failed to reset signal {signalNumber} disposition to default: still ignored.");
        }
    }

    internal static IntPtr? ReadSignalHandlerOrNull(int signalNumber)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        return NativeMethods.TryReadSignalHandler(signalNumber, out var handler)
            ? handler
            : null;
    }

    internal static bool ReexecOnceIfSignalRegistrationUnavailable(
        IntPtr? sigtermBefore,
        IntPtr? sigintBefore,
        IntPtr? sighupBefore)
    {
        return TryRunSignalBootstrap(
            isLinux: OperatingSystem.IsLinux(),
            guardValue: Environment.GetEnvironmentVariable(SignalBootstrapReexecEnv),
            needsBootstrap: () =>
                NeedsSignalBootstrapReexec(SigTerm, sigtermBefore)
                || NeedsSignalBootstrapReexec(SigInt, sigintBefore)
                || NeedsSignalBootstrapReexec(SigHup, sighupBefore),
            readArgv: TryReadCurrentArgv,
            setGuard: SetSignalBootstrapGuard,
            exec: NativeMethods.ExecVp);
    }

    internal static void SetSignalBootstrapGuard(string? value)
    {
        Environment.SetEnvironmentVariable(SignalBootstrapReexecEnv, value);
        if (OperatingSystem.IsLinux())
            NativeMethods.SetEnvironmentVariable(SignalBootstrapReexecEnv, value);
    }

    internal static bool TryRunSignalBootstrap(
        bool isLinux,
        string? guardValue,
        Func<bool> needsBootstrap,
        Func<string[]?> readArgv,
        Action<string?> setGuard,
        Action<string[]> exec)
    {
        ArgumentNullException.ThrowIfNull(needsBootstrap);
        ArgumentNullException.ThrowIfNull(readArgv);
        ArgumentNullException.ThrowIfNull(setGuard);
        ArgumentNullException.ThrowIfNull(exec);

        if (!isLinux
            || string.Equals(guardValue, "1", StringComparison.Ordinal)
            || !needsBootstrap())
        {
            return false;
        }

        var argv = readArgv();
        if (argv is null || argv.Length == 0)
            return false;

        // CoreCLR can remember a startup-ignored SIGINT before Bridge.RunAsync
        // gets control; after the reset above, the first runtime may still
        // decline to install a catchable SIGINT handler. Re-exec once before
        // publishing any stdout envelopes so the runtime starts from the
        // now-default dispositions. exec preserves pid and stdio fds, so the
        // host's process and pipe tracking remain valid.
        setGuard("1");
        try
        {
            exec(argv);
        }
        catch (Exception ex)
        {
            setGuard(null);
            throw new InvalidOperationException("Failed to re-exec signal bootstrap.", ex);
        }

        return true;
    }

    private static bool NeedsSignalBootstrapReexec(int signalNumber, IntPtr? handlerBeforeRegistration)
    {
        if (handlerBeforeRegistration is not { } before
            || (before != NativeMethods.SigDfl && before != NativeMethods.SigIgn)
            || !NativeMethods.TryReadSignalHandler(signalNumber, out var after))
        {
            return false;
        }

        return after == NativeMethods.SigDfl || after == NativeMethods.SigIgn;
    }

    internal static string[]? ParseProcCmdlineArgv(byte[] bytes)
    {
        var args = new List<string>();
        var start = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != 0)
                continue;

            args.Add(Encoding.UTF8.GetString(bytes, start, i - start));
            start = i + 1;
        }

        if (start < bytes.Length)
            args.Add(Encoding.UTF8.GetString(bytes, start, bytes.Length - start));

        return args.Count == 0 ? null : args.ToArray();
    }

    internal static string[]? TryReadCurrentArgv()
    {
        try
        {
            return ParseProcCmdlineArgv(File.ReadAllBytes("/proc/self/cmdline"));
        }
        catch
        {
            return null;
        }
    }

    private static class NativeMethods
    {
        internal static readonly IntPtr SigDfl = IntPtr.Zero;
        internal static readonly IntPtr SigIgn = new(1);

        private const int SigActionBufferBytes = 256;

        [DllImport("libc", EntryPoint = "sigaction", SetLastError = true)]
        private static extern int SigAction(int sig, IntPtr act, IntPtr oldact);

        [DllImport("libc", EntryPoint = "signal", SetLastError = true)]
        internal static extern IntPtr Signal(int sig, IntPtr handler);

        [DllImport("libc", EntryPoint = "execvp", SetLastError = true)]
        private static extern int ExecVp(IntPtr file, IntPtr argv);

        [DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
        private static extern int SetEnv(IntPtr name, IntPtr value, int overwrite);

        [DllImport("libc", EntryPoint = "unsetenv", SetLastError = true)]
        private static extern int UnsetEnv(IntPtr name);

        internal static bool TryReadSignalHandler(int signalNumber, out IntPtr handler)
        {
            handler = IntPtr.Zero;
            var oldAction = Marshal.AllocHGlobal(SigActionBufferBytes);
            try
            {
                if (SigAction(signalNumber, IntPtr.Zero, oldAction) != 0)
                    return false;

                handler = Marshal.ReadIntPtr(oldAction);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(oldAction);
            }
        }

        internal static void SetEnvironmentVariable(string name, string? value)
        {
            var namePtr = IntPtr.Zero;
            var valuePtr = IntPtr.Zero;
            try
            {
                namePtr = Marshal.StringToHGlobalAnsi(name);
                if (value is null)
                {
                    _ = UnsetEnv(namePtr);
                    return;
                }

                valuePtr = Marshal.StringToHGlobalAnsi(value);
                _ = SetEnv(namePtr, valuePtr, overwrite: 1);
            }
            catch
            {
            }
            finally
            {
                if (valuePtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(valuePtr);
                if (namePtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(namePtr);
            }
        }

        internal static void ExecVp(string[] argv)
        {
            var argPointers = new IntPtr[argv.Length];
            var argvBlock = IntPtr.Zero;
            try
            {
                for (var i = 0; i < argv.Length; i++)
                    argPointers[i] = Marshal.StringToHGlobalAnsi(argv[i]);

                argvBlock = Marshal.AllocHGlobal(IntPtr.Size * (argv.Length + 1));
                for (var i = 0; i < argPointers.Length; i++)
                    Marshal.WriteIntPtr(argvBlock, i * IntPtr.Size, argPointers[i]);
                Marshal.WriteIntPtr(argvBlock, argPointers.Length * IntPtr.Size, IntPtr.Zero);

                var res = ExecVp(argPointers[0], argvBlock);
                if (res == -1)
                {
                    var errno = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException($"execvp failed with error code: {errno}");
                }
                
                throw new InvalidOperationException($"execvp returned unexpectedly with code: {res}");
            }
            finally
            {
                if (argvBlock != IntPtr.Zero)
                    Marshal.FreeHGlobal(argvBlock);
                foreach (var argPointer in argPointers)
                {
                    if (argPointer != IntPtr.Zero)
                        Marshal.FreeHGlobal(argPointer);
                }
            }
        }
    }
}
