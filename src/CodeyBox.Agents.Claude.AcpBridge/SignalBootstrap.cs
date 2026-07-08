using System;
using System.Collections.Generic;
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
    internal const string SignalBootstrapGuardSetValue = "1";

    private static readonly ShutdownSignal[] ShutdownSignals =
    {
        new(PosixSignal.SIGTERM, SigTerm, "SIGTERM"),
        new(PosixSignal.SIGINT, SigInt, "SIGINT"),
        new(PosixSignal.SIGHUP, SigHup, "SIGHUP"),
    };

    internal static PosixSignalRegistration[] RegisterShutdownHandlers(
        Action<PosixSignalContext> shutdown,
        bool enableReexec)
    {
        ArgumentNullException.ThrowIfNull(shutdown);

        var beforeRegistration = CaptureSignalHandlersBeforeRegistration();
        var registrations = new PosixSignalRegistration[ShutdownSignals.Length];
        var registered = 0;

        try
        {
            for (var i = 0; i < ShutdownSignals.Length; i++)
            {
                ResetInheritedIgnoredSignalToDefault(ShutdownSignals[i]);
                registrations[i] = PosixSignalRegistration.Create(ShutdownSignals[i].PosixSignal, shutdown);
                registered++;
            }

            try
            {
                if (enableReexec)
                    _ = RunSignalBootstrapIfNeeded(beforeRegistration);
            }
            finally
            {
                SetSignalBootstrapGuard(null);
            }

            return registrations;
        }
        catch
        {
            for (var i = 0; i < registered; i++)
                registrations[i].Dispose();
            throw;
        }
    }

    internal static void ResetInheritedIgnoredSignalToDefault(int signalNumber)
    {
        ResetInheritedIgnoredSignalToDefault(new ShutdownSignal((PosixSignal)signalNumber, signalNumber, signalNumber.ToString()));
    }

    internal static IntPtr? ReadSignalHandlerOrNull(int signalNumber)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        return NativeMethods.ReadSignalHandler(signalNumber);
    }

    internal static bool RunSignalBootstrapIfNeeded(
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
            || string.Equals(guardValue, SignalBootstrapGuardSetValue, StringComparison.Ordinal)
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
        setGuard(SignalBootstrapGuardSetValue);
        try
        {
            exec(argv);
        }
        catch (Exception ex)
        {
            try
            {
                setGuard(null);
            }
            catch (Exception clearEx)
            {
                throw new InvalidOperationException(
                    "Failed to clear signal bootstrap guard after re-exec failure.",
                    new AggregateException(ex, clearEx));
            }

            throw new InvalidOperationException("Failed to re-exec signal bootstrap.", ex);
        }

        return true;
    }

    internal static bool RunSignalBootstrapIfNeeded(Func<bool> needsBootstrap)
    {
        return RunSignalBootstrapIfNeeded(
            isLinux: OperatingSystem.IsLinux(),
            guardValue: Environment.GetEnvironmentVariable(SignalBootstrapReexecEnv),
            needsBootstrap: needsBootstrap,
            readArgv: ReadCurrentArgv,
            setGuard: SetSignalBootstrapGuard,
            exec: NativeMethods.ExecVp);
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

    private static SignalHandlerSnapshot[] CaptureSignalHandlersBeforeRegistration()
    {
        var snapshots = new SignalHandlerSnapshot[ShutdownSignals.Length];
        for (var i = 0; i < ShutdownSignals.Length; i++)
        {
            var handler = OperatingSystem.IsLinux()
                ? NativeMethods.ReadSignalHandler(ShutdownSignals[i].Number)
                : (IntPtr?)null;
            snapshots[i] = new SignalHandlerSnapshot(ShutdownSignals[i], handler);
        }

        return snapshots;
    }

    private static void ResetInheritedIgnoredSignalToDefault(ShutdownSignal signal)
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Detached launchers can inherit SIG_IGN for SIGHUP/SIGINT/SIGTERM.
        // PosixSignalRegistration honours inherited ignores and will not make
        // such a signal catchable, so reset only that disposition before
        // registering the bridge's shutdown handler.
        var handler = NativeMethods.ReadSignalHandler(signal.Number);
        if (handler != NativeMethods.SigIgn)
            return;

        IntPtr prev;
        try
        {
            prev = NativeMethods.Signal(signal.Number, NativeMethods.SigDfl);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to invoke signal P/Invoke for {signal.Name}.", ex);
        }

        if (prev == new IntPtr(-1))
        {
            var errno = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"signal(2) failed to reset {signal.Name} disposition to default. errno={errno}.");
        }

        var afterHandler = NativeMethods.ReadSignalHandler(signal.Number);
        if (afterHandler == NativeMethods.SigIgn)
            throw new InvalidOperationException($"Failed to reset {signal.Name} disposition to default: still ignored.");
    }

    private static bool RunSignalBootstrapIfNeeded(IReadOnlyList<SignalHandlerSnapshot> beforeRegistration)
    {
        return RunSignalBootstrapIfNeeded(() => NeedsSignalBootstrapReexec(beforeRegistration));
    }

    private static bool NeedsSignalBootstrapReexec(IReadOnlyList<SignalHandlerSnapshot> beforeRegistration)
    {
        for (var i = 0; i < beforeRegistration.Count; i++)
        {
            var snapshot = beforeRegistration[i];
            if (snapshot.HandlerBeforeRegistration is not { } before
                || (before != NativeMethods.SigDfl && before != NativeMethods.SigIgn))
            {
                continue;
            }

            var after = NativeMethods.ReadSignalHandler(snapshot.Signal.Number);
            if (after == NativeMethods.SigDfl || after == NativeMethods.SigIgn)
                return true;
        }

        return false;
    }

    private static string[]? ReadCurrentArgv()
    {
        try
        {
            return ParseProcCmdlineArgv(File.ReadAllBytes("/proc/self/cmdline"));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to read /proc/self/cmdline for signal bootstrap re-exec.", ex);
        }
    }

    private static void SetSignalBootstrapGuard(string? value)
    {
        Environment.SetEnvironmentVariable(SignalBootstrapReexecEnv, value);
        if (OperatingSystem.IsLinux())
            NativeMethods.SetEnvironmentVariable(SignalBootstrapReexecEnv, value);
    }

    private readonly record struct ShutdownSignal(PosixSignal PosixSignal, int Number, string Name);

    private readonly record struct SignalHandlerSnapshot(ShutdownSignal Signal, IntPtr? HandlerBeforeRegistration);

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

        internal static IntPtr ReadSignalHandler(int signalNumber)
        {
            var oldAction = IntPtr.Zero;
            try
            {
                oldAction = Marshal.AllocHGlobal(SigActionBufferBytes);
                if (SigAction(signalNumber, IntPtr.Zero, oldAction) != 0)
                {
                    var errno = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(
                        $"sigaction(2) failed while reading signal {signalNumber} disposition. errno={errno}.");
                }

                return Marshal.ReadIntPtr(oldAction);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to inspect signal {signalNumber} disposition.",
                    ex);
            }
            finally
            {
                if (oldAction != IntPtr.Zero)
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
                    var unsetResult = UnsetEnv(namePtr);
                    if (unsetResult != 0)
                    {
                        var errno = Marshal.GetLastWin32Error();
                        throw new InvalidOperationException(
                            $"unsetenv(3) failed for {name}. errno={errno}.");
                    }

                    return;
                }

                valuePtr = Marshal.StringToHGlobalAnsi(value);
                var setResult = SetEnv(namePtr, valuePtr, overwrite: 1);
                if (setResult != 0)
                {
                    var errno = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(
                        $"setenv(3) failed for {name}. errno={errno}.");
                }
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
