using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace UBVerification
{
    public sealed class DesktopSnapshot
    {
        public string capturedUtc, observationSource, windowStation, runnerDesktop, inputDesktop, expectedDesktop;
        public bool currentDesktopQuerySucceeded, windowStationQuerySucceeded, inputDesktopQuerySucceeded;
        public int currentDesktopQueryError, windowStationQueryError, inputDesktopQueryError;
        public bool inputDesktopCloseSucceeded, ownedWindowsQuerySucceeded, complete, isolated;
        public int inputDesktopCloseError, ownedWindowsQueryError;
        public bool processThreadQuerySucceeded, processThreadCohortStable, nonInputIsolated;
        public string processThreadQueryError;
        public uint[] processThreadIds, processThreadIdsAfter;
        public OwnedUiThreadSnapshot[] ownedUiThreads;
        public ProcessUiScan[] processUiScanAttempts;
        public BooleanQuery runnerInputQuery;
        public bool? runnerReceivesInput;
        public int processId;
        public uint nativeThreadId;
        public OwnedWindowSnapshot[] ownedWindows;
        public GuiThreadSnapshot guiThread;
        public FocusPreparationReceipt focusPreparation;
        public FocusActivationReceipt focusActivation;
    }

    public sealed class OwnedUiThreadSnapshot
    {
        public uint nativeThreadId;
        public bool enumerationReturned, querySucceeded;
        public int errorCode, windowCount;
        public string error;
    }
    public sealed class ProcessThreadSnapshot
    {
        public string source = "PssCaptureSnapshot/PSS_CAPTURE_THREADS", error;
        public uint captureFlags = 0x80, contextFlags, capturedThreadCount, contextLength, walkedEntries;
        public uint captureCode = UInt32.MaxValue, queryCode = UInt32.MaxValue, markerCreateCode = UInt32.MaxValue,
            walkEndCode = UInt32.MaxValue, markerFreeCode = UInt32.MaxValue, snapshotFreeCode = UInt32.MaxValue;
        public int entryBytes;
        public bool snapshotAcquired, markerAcquired, currentThreadPresent, released, succeeded;
        public double elapsedMilliseconds;
    }

    public sealed class ProcessUiScan
    {
        public int attempt;
        public double elapsedMilliseconds;
        public bool querySucceeded, cohortStable;
        public uint[] beforeThreadIds, afterThreadIds;
        public ProcessThreadSnapshot beforeThreadSnapshot, afterThreadSnapshot;
        public OwnedUiThreadSnapshot[] threads;
        public OwnedWindowSnapshot[] windows;
        public string error;
    }

    public sealed class FocusPreparationReceipt
    {
        public long gameWindow, peerWindow;
        public bool? completed;
        public string error;
        public DesktopSnapshot[] steps;
        public GuiThreadSnapshot finalGuiThread;
    }

    public sealed class FocusActivationReceipt
    {
        public string stimulus = "same-thread-owned-non-input-peer-activation";
        public long previousActiveWindow;
        public int activationError;
        public bool attempted, completed;
        public string error;
        public DesktopSnapshot before, after;
    }

    public sealed class BooleanQuery
    {
        public bool succeeded;
        public int errorCode;
        public uint bytesNeeded;
        public int? rawValue;
        public bool? value;
    }

    public sealed class OwnedWindowSnapshot
    {
        public long windowHandle;
        public uint processId, nativeThreadId;
        public string desktop;
        public bool desktopQuerySucceeded, visible, minimized, clientRectQuerySucceeded, identityQuerySucceeded;
        public int identityQueryError;
        public int desktopQueryError, clientRectQueryError, clientWidth, clientHeight;
        public BooleanQuery inputQuery;
    }

    public sealed class GuiThreadSnapshot
    {
        public bool querySucceeded;
        public int queryError;
        public uint nativeThreadId, flags;
        public long activeWindow, focusWindow;
    }

    public sealed class ForegroundSnapshot
    {
        public string capturedUtc;
        public uint processId, nativeThreadId;
        public long windowHandle;
    }

    public sealed class DesktopLaunch : IDisposable
    {
        public Process Process { get; internal set; }
        public string DesktopName { get; internal set; }
        public string WindowStationName { get; internal set; }
        public uint PrimaryThreadId { get; internal set; }
        internal IntPtr Handle;
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            if (Process != null && !Process.HasExited)
                throw new InvalidOperationException("Stop the owned game before releasing its process handle.");
            if (Handle != IntPtr.Zero)
            {
                DesktopReader.ReleaseDesktop(Handle);
                Handle = IntPtr.Zero;
            }
            if (Process != null) Process.Dispose();
            disposed = true;
        }
    }

    public static class DesktopReader
    {
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] private static extern uint PssCaptureSnapshot(IntPtr process, uint flags, uint contextFlags, out IntPtr snapshot);
        [DllImport("kernel32.dll")] private static extern uint PssQuerySnapshot(IntPtr snapshot, uint informationClass, out PssThreadInformation information, uint length);
        [DllImport("kernel32.dll")] private static extern uint PssWalkMarkerCreate(IntPtr allocator, out IntPtr marker);
        [DllImport("kernel32.dll")] private static extern uint PssWalkSnapshot(IntPtr snapshot, uint informationClass, IntPtr marker, out PssThreadEntry entry, uint length);
        [DllImport("kernel32.dll")] private static extern uint PssWalkMarkerFree(IntPtr marker);
        [DllImport("kernel32.dll")] private static extern uint PssFreeSnapshot(IntPtr process, IntPtr snapshot);
        [StructLayout(LayoutKind.Sequential)]
        private struct PssFileTime { public uint low, high; }
        [StructLayout(LayoutKind.Sequential)]
        private struct PssThreadInformation { public uint threadsCaptured, contextLength; }
        [StructLayout(LayoutKind.Sequential)]
        private struct PssThreadEntry
        {
            public uint exitStatus;
            public IntPtr tebBaseAddress;
            public uint processId, threadId;
            public UIntPtr affinityMask;
            public int priority, basePriority;
            public IntPtr lastSyscallFirstArgument;
            public ushort lastSyscallNumber;
            public PssFileTime createTime, exitTime, kernelTime, userTime;
            public IntPtr win32StartAddress;
            public PssFileTime captureTime;
            public uint flags;
            public ushort suspendCount, sizeOfContextRecord;
            public IntPtr contextRecord;
        }
        [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
        [DllImport("user32.dll", SetLastError=true)] private static extern bool EnumThreadWindows(uint threadId, WindowEnumerator callback, IntPtr parameter);
        [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr GetProcessWindowStation();
        [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError=true)] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", EntryPoint="CreateDesktopW", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SetActiveWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", EntryPoint="CreateWindowExW", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", SetLastError=true)] private static extern bool DestroyWindow(IntPtr window);
        [StructLayout(LayoutKind.Sequential)]
        private struct ClientRect { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public uint size, flags;
            public IntPtr active, focus, capture, menuOwner, moveSize, caret;
            public ClientRect caretRectangle;
        }
        [DllImport("user32.dll", SetLastError=true)]
        private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo information);
        [DllImport("user32.dll", SetLastError=true)]
        private static extern bool GetClientRect(IntPtr window, out ClientRect rectangle);
        private delegate bool WindowEnumerator(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll", SetLastError=true)]
        private static extern bool EnumDesktopWindows(IntPtr desktop, WindowEnumerator callback, IntPtr parameter);
        [DllImport("user32.dll", SetLastError=true)] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", EntryPoint="GetUserObjectInformationW", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern bool ObjectName(IntPtr handle, int index, StringBuilder value, uint length, out uint needed);
        [DllImport("user32.dll", EntryPoint="GetUserObjectInformationW", SetLastError=true)]
        private static extern bool ObjectInput(IntPtr handle, int index, out int value, uint length, out uint needed);
        [DllImport("kernel32.dll", EntryPoint="CreateProcessW", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
            ref StartupInfo startup, out ProcessInfo process);
        [DllImport("kernel32.dll", SetLastError=true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError=true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError=true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string reserved, desktop, title;
            public uint x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, flags;
            public ushort showWindow, reservedBytes;
            public IntPtr reservedPointer, input, output, error;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInfo { public IntPtr process, thread; public uint processId, threadId; }
        private static IntPtr focusGameWindow;
        private static IntPtr focusPeer;
        private static FocusPreparationReceipt focusPreparation;
        private static FocusActivationReceipt focusActivation;

        public static uint CurrentNativeThreadId() { return GetCurrentThreadId(); }

        private static void RequireOwnWindow(IntPtr window)
        {
            uint owner;
            if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out owner) == 0 ||
                owner != Process.GetCurrentProcess().Id)
                throw new InvalidOperationException("The test window does not belong to this game process.");
        }

        private static IntPtr FindCurrentRenderWindow(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException("width", "The render area must be positive.");
            var window = IntPtr.Zero;
            int processId;
            using (var process = Process.GetCurrentProcess()) processId = process.Id;
            var count = 0;
            var sizes = new StringBuilder();
            var desktop = RequireHandle(GetThreadDesktop(GetCurrentThreadId()), "GetThreadDesktop(own window)");
            if (!EnumDesktopWindows(desktop, (candidate, parameter) => {
                uint owner;
                if (GetWindowThreadProcessId(candidate, out owner) != 0 && owner == processId && IsWindowVisible(candidate))
                {
                    ClientRect rectangle;
                    if (GetClientRect(candidate, out rectangle))
                    {
                        var candidateWidth = rectangle.right - rectangle.left;
                        var candidateHeight = rectangle.bottom - rectangle.top;
                        sizes.Append(candidateWidth).Append('x').Append(candidateHeight).Append(';');
                        if (candidateWidth == width && candidateHeight == height)
                        {
                            window = candidate;
                            count++;
                        }
                    }
                }
                return true;
            }, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumDesktopWindows(own window)");
            if (count != 1)
                throw new InvalidOperationException("The game has no unique owned render window: matching=" + count +
                    "; expected=" + width + "x" + height + "; visibleSizes=" + sizes);
            RequireOwnWindow(window);
            uint windowProcessId;
            if (GetWindowThreadProcessId(window, out windowProcessId) != GetCurrentThreadId())
                throw new InvalidOperationException("The render window is not owned by the current game thread.");
            return window;
        }

        public static GuiThreadSnapshot ReadGuiThread()
        {
            var thread = GetCurrentThreadId();
            var information = new GuiThreadInfo { size = (uint)Marshal.SizeOf(typeof(GuiThreadInfo)) };
            var succeeded = GetGUIThreadInfo(thread, ref information);
            return new GuiThreadSnapshot {
                nativeThreadId = thread, querySucceeded = succeeded,
                queryError = succeeded ? 0 : Marshal.GetLastWin32Error(),
                flags = information.flags, activeWindow = information.active.ToInt64(),
                focusWindow = information.focus.ToInt64()
            };
        }

        public static void PrepareCurrentWindowFocusLoss(int width, int height)
        {
            focusPreparation = null;
            focusActivation = null;
            var desktop = ReadCurrent(Name(RequireHandle(GetThreadDesktop(GetCurrentThreadId()), "GetThreadDesktop(owned observation)")), "focus-preparation");
            RequireFocusDesktop(desktop);
            if (focusGameWindow != IntPtr.Zero || focusPeer != IntPtr.Zero)
                throw new InvalidOperationException("A focus verification window is already owned.");
            var window = FindCurrentRenderWindow(width, height);
            if (IsIconic(window)) throw new InvalidOperationException("The render window is already minimized.");
            focusGameWindow = window;
            var receipt = new FocusPreparationReceipt { gameWindow = window.ToInt64() };
            var steps = new List<DesktopSnapshot>(10);
            try
            {
                focusPeer = RequireHandle(CreateWindowEx(0, "STATIC", "UB Verification Activation", 0x00CF0000,
                    0, 0, 160, 80, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), "CreateWindowExW(focus peer)");
                receipt.peerWindow = focusPeer.ToInt64();
                var windows = new[] { focusPeer, window, window, focusPeer, focusPeer };
                var commands = new[] { 4, 6, 4, 6, 4 };
                var operations = new[] { "peer-show", "game-minimize", "game-show", "peer-minimize", "peer-reshow" };
                // Let Windows select the render window when it is not already active.
                // Every show call has a bounded before/after receipt.
                for (var step = 0; step < 10; step++)
                {
                    if (step == 2 && steps[1].guiThread.activeWindow == window.ToInt64()) break;
                    var operation = step / 2;
                    var after = (step & 1) != 0;
                    if (after) ShowWindow(windows[operation], commands[operation]);
                    var sample = ReadCurrent(Name(RequireHandle(GetThreadDesktop(GetCurrentThreadId()), "GetThreadDesktop(owned observation)")), "focus-preparation/" + operations[operation] +
                        (after ? "/after" : "/before"));
                    sample.focusPreparation = null;
                    steps.Add(sample);
                    RequireFocusWindows(sample, receipt.gameWindow, receipt.peerWindow, false);
                }
                var gui = ReadGuiThread();
                receipt.finalGuiThread = gui;
                if (!gui.querySucceeded || gui.activeWindow != window.ToInt64())
                    throw new InvalidOperationException("Windows did not activate the owned render window during focus preparation.");
                receipt.completed = true;
            }
            catch (Exception error)
            {
                receipt.completed = false;
                receipt.error = error.ToString();
                throw;
            }
            finally
            {
                receipt.steps = steps.ToArray();
                focusPreparation = receipt;
            }
        }

        private static void RequireFocusDesktop(DesktopSnapshot snapshot)
        {
            var verified = HasNonInputOwnershipProof(snapshot, snapshot.runnerDesktop, snapshot.processId, snapshot.nativeThreadId);
            if (!verified || !snapshot.guiThread.querySucceeded ||
                snapshot.runnerInputQuery == null || !snapshot.runnerInputQuery.succeeded ||
                snapshot.runnerInputQuery.bytesNeeded != 4 || snapshot.runnerInputQuery.rawValue != 0 ||
                !System.Text.RegularExpressions.Regex.IsMatch(snapshot.runnerDesktop ?? "", "^UBGameTest_[a-f0-9]{32}$"))
                throw new InvalidOperationException("Focus verification requires the owned non-input desktop.");
        }

        private static void RequireFocusWindows(DesktopSnapshot snapshot, long game, long peer, bool visible)
        {
            RequireFocusDesktop(snapshot);
            var observed = 0;
            foreach (var owned in snapshot.ownedWindows)
            {
                if (owned.windowHandle != game && owned.windowHandle != peer) continue;
                if (game == 0 || peer == 0 || game == peer || owned.processId != snapshot.processId ||
                    owned.nativeThreadId != snapshot.nativeThreadId || !owned.desktopQuerySucceeded ||
                    owned.desktop != snapshot.runnerDesktop || !owned.clientRectQuerySucceeded ||
                    owned.inputQuery == null || !owned.inputQuery.succeeded || owned.inputQuery.bytesNeeded != 4 ||
                    owned.inputQuery.rawValue != 0 || visible && (!owned.visible || owned.minimized))
                    throw new InvalidOperationException("The focus windows must belong to the current game thread on its non-input desktop.");
                observed++;
            }
            if (observed != 2)
                throw new InvalidOperationException("The focus windows were not both observed on the current game thread.");
        }

        public static void DeactivateCurrentWindow(int width, int height)
        {
            focusActivation = null;
            var receipt = new FocusActivationReceipt();
            try
            {
                var before = ReadCurrent(Name(RequireHandle(GetThreadDesktop(GetCurrentThreadId()), "GetThreadDesktop(owned observation)")), "focus-activation/before");
                before.focusActivation = null;
                receipt.before = before;
                RequireFocusDesktop(before);
                var window = FindCurrentRenderWindow(width, height);
                if (window != focusGameWindow || focusPeer == IntPtr.Zero || focusPreparation == null || focusPreparation.completed != true)
                    throw new InvalidOperationException("The owned focus peer has not been prepared.");
                RequireFocusWindows(before, window.ToInt64(), focusPeer.ToInt64(), true);
                if (before.guiThread.activeWindow != window.ToInt64())
                    throw new InvalidOperationException("The owned render window is not active before the focus-loss stimulus.");
                // This affects only this thread's queue on the verified non-input desktop.
                // Foreground activation, thread-input attachment and input injection are not used.
                receipt.attempted = true;
                var previous = SetActiveWindow(focusPeer);
                receipt.activationError = previous == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                receipt.previousActiveWindow = previous.ToInt64();
                var after = ReadCurrent(Name(RequireHandle(GetThreadDesktop(GetCurrentThreadId()), "GetThreadDesktop(owned observation)")), "focus-activation/after");
                after.focusActivation = null;
                receipt.after = after;
                RequireFocusWindows(after, window.ToInt64(), focusPeer.ToInt64(), true);
                if (previous != window || after.guiThread.activeWindow != focusPeer.ToInt64() ||
                    after.guiThread.focusWindow == window.ToInt64())
                    throw new InvalidOperationException("Windows did not deactivate the owned render window in favor of its same-thread peer.");
                receipt.completed = true;
            }
            catch (Exception error)
            {
                receipt.error = error.ToString();
                throw;
            }
            finally { focusActivation = receipt; }
        }

        public static void ShowCurrentWindowWithoutActivation()
        {
            if (focusGameWindow == IntPtr.Zero) throw new InvalidOperationException("No focus verification window is owned.");
            RequireOwnWindow(focusGameWindow);
            ShowWindow(focusGameWindow, 4);
            if (IsIconic(focusGameWindow)) throw new InvalidOperationException("The owned render window remained minimized.");
        }

        public static bool CurrentWindowIsDeactivated(int width, int height)
        {
            var window = FindCurrentRenderWindow(width, height);
            RequireOwnWindow(focusPeer);
            var gui = ReadGuiThread();
            if (!gui.querySucceeded) throw new Win32Exception(gui.queryError, "GetGUIThreadInfo(focus loss)");
            return window == focusGameWindow && !IsIconic(window) &&
                gui.activeWindow == focusPeer.ToInt64() && gui.focusWindow != window.ToInt64();
        }

        public static void RestoreCurrentWindow()
        {
            if (focusGameWindow != IntPtr.Zero)
            {
                ShowCurrentWindowWithoutActivation();
                focusGameWindow = IntPtr.Zero;
            }
            if (focusPeer != IntPtr.Zero)
            {
                RequireOwnWindow(focusPeer);
                if (!DestroyWindow(focusPeer)) throw new Win32Exception(Marshal.GetLastWin32Error(), "DestroyWindow(focus peer)");
                focusPeer = IntPtr.Zero;
            }
        }

        private static IntPtr RequireHandle(IntPtr handle, string operation)
        {
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
            return handle;
        }

        private static string Name(IntPtr handle)
        {
            var value = new StringBuilder(1024);
            uint needed;
            if (!ObjectName(handle, 2, value, (uint)(value.Capacity * 2), out needed))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetUserObjectInformationW(UOI_NAME)");
            if (value.Length == 0) throw new InvalidOperationException("The desktop name is empty.");
            return value.ToString();
        }

        private static BooleanQuery ReadInputState(IntPtr desktop)
        {
            int receivesInput;
            uint needed;
            var succeeded = ObjectInput(desktop, 6, out receivesInput, 4, out needed);
            var error = succeeded ? 0 : Marshal.GetLastWin32Error();
            return new BooleanQuery {
                succeeded = succeeded, errorCode = error, bytesNeeded = needed,
                rawValue = succeeded ? (int?)receivesInput : null,
                value = succeeded ? (bool?)(receivesInput != 0) : null
            };
        }

        public static DesktopSnapshot ReadCurrent(string expectedDesktop, string observationSource)
        {
            var snapshot = new DesktopSnapshot {
                capturedUtc = DateTime.UtcNow.ToString("O"), nativeThreadId = GetCurrentThreadId(),
                expectedDesktop = expectedDesktop, observationSource = observationSource,
                ownedWindows = new OwnedWindowSnapshot[0], guiThread = ReadGuiThread()
            };
            using (var process = Process.GetCurrentProcess()) snapshot.processId = process.Id;
            var current = GetThreadDesktop(snapshot.nativeThreadId);
            snapshot.currentDesktopQueryError = current == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            if (current != IntPtr.Zero)
            {
                try { snapshot.runnerDesktop = Name(current); snapshot.currentDesktopQuerySucceeded = true; }
                catch (Win32Exception ex) { snapshot.currentDesktopQueryError = ex.NativeErrorCode; }
                snapshot.runnerInputQuery = ReadInputState(current);
                snapshot.runnerReceivesInput = snapshot.runnerInputQuery.value;
            }
            var station = GetProcessWindowStation();
            snapshot.windowStationQueryError = station == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            if (station != IntPtr.Zero)
                try { snapshot.windowStation = Name(station); snapshot.windowStationQuerySucceeded = true; }
                catch (Win32Exception ex) { snapshot.windowStationQueryError = ex.NativeErrorCode; }
            var input = OpenInputDesktop(0, false, 0x0001);
            snapshot.inputDesktopQueryError = input == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            if (input != IntPtr.Zero)
            {
                try { snapshot.inputDesktop = Name(input); snapshot.inputDesktopQuerySucceeded = true; }
                catch (Win32Exception ex) { snapshot.inputDesktopQueryError = ex.NativeErrorCode; }
                finally {
                    snapshot.inputDesktopCloseSucceeded = CloseDesktop(input);
                    snapshot.inputDesktopCloseError = snapshot.inputDesktopCloseSucceeded ? 0 : Marshal.GetLastWin32Error();
                }
            }
            if (String.IsNullOrEmpty(expectedDesktop))
            {
                if (current != IntPtr.Zero)
                {
                    var windows = new List<OwnedWindowSnapshot>();
                    snapshot.ownedWindowsQuerySucceeded = EnumDesktopWindows(current, (window, parameter) => {
                        uint owner;
                        var thread = GetWindowThreadProcessId(window, out owner);
                        if (thread == 0 || owner != snapshot.processId) return true;
                        var row = new OwnedWindowSnapshot { windowHandle = window.ToInt64(), processId = owner,
                            nativeThreadId = thread, visible = IsWindowVisible(window), minimized = IsIconic(window) };
                        var desktop = GetThreadDesktop(thread);
                        row.desktopQueryError = desktop == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                        if (desktop != IntPtr.Zero)
                        {
                            try { row.desktop = Name(desktop); row.desktopQuerySucceeded = true; }
                            catch (Win32Exception ex) { row.desktopQueryError = ex.NativeErrorCode; }
                            row.inputQuery = ReadInputState(desktop);
                        }
                        ClientRect rectangle;
                        row.clientRectQuerySucceeded = GetClientRect(window, out rectangle);
                        row.clientRectQueryError = row.clientRectQuerySucceeded ? 0 : Marshal.GetLastWin32Error();
                        if (row.clientRectQuerySucceeded) {
                            row.clientWidth = rectangle.right - rectangle.left;
                            row.clientHeight = rectangle.bottom - rectangle.top;
                        }
                        windows.Add(row);
                        return true;
                    }, IntPtr.Zero);
                    snapshot.ownedWindowsQueryError = snapshot.ownedWindowsQuerySucceeded ? 0 : Marshal.GetLastWin32Error();
                    snapshot.ownedWindows = windows.ToArray();
                }
            }
            else ReadOwnedProcessWindows(snapshot);
            snapshot.complete = snapshot.currentDesktopQuerySucceeded && snapshot.windowStationQuerySucceeded &&
                snapshot.inputDesktopQuerySucceeded && snapshot.inputDesktopCloseSucceeded &&
                snapshot.runnerInputQuery != null && snapshot.runnerInputQuery.succeeded &&
                snapshot.runnerInputQuery.bytesNeeded == 4 && snapshot.ownedWindowsQuerySucceeded;
            snapshot.isolated = snapshot.complete && snapshot.runnerReceivesInput == false &&
                !String.Equals(snapshot.runnerDesktop, snapshot.inputDesktop, StringComparison.OrdinalIgnoreCase) &&
                (String.IsNullOrEmpty(expectedDesktop) ||
                    String.Equals(snapshot.runnerDesktop, expectedDesktop, StringComparison.OrdinalIgnoreCase));
            snapshot.nonInputIsolated = HasNonInputOwnershipProof(snapshot, expectedDesktop, snapshot.processId, snapshot.nativeThreadId);
            if (!String.IsNullOrEmpty(expectedDesktop)) snapshot.isolated = snapshot.isolated && snapshot.nonInputIsolated;
            snapshot.focusPreparation = focusPreparation;
            snapshot.focusActivation = focusActivation;
            return snapshot;
        }

        private static uint[] CurrentProcessThreadIds(out ProcessThreadSnapshot acquisition)
        {
            acquisition = new ProcessThreadSnapshot();
            var timer = Stopwatch.StartNew();
            var ids = new List<uint>();
            var unique = new HashSet<uint>();
            var process = GetCurrentProcess();
            var processId = GetCurrentProcessId();
            var currentThread = GetCurrentThreadId();
            var snapshot = IntPtr.Zero;
            var marker = IntPtr.Zero;
            Exception failure = null;
            try
            {
                acquisition.entryBytes = Marshal.SizeOf(typeof(PssThreadEntry));
                // FILETIME has DWORD alignment. An Int64 here changes the ABI.
                if (acquisition.entryBytes != (IntPtr.Size == 8 ? 120 : 92) ||
                    Marshal.OffsetOf(typeof(PssThreadEntry), "threadId").ToInt32() != (IntPtr.Size == 8 ? 20 : 12))
                    throw new InvalidOperationException("The native PSS thread entry layout is invalid.");
                acquisition.captureCode = PssCaptureSnapshot(process, 0x80, 0, out snapshot);
                if (acquisition.captureCode != 0) throw new Win32Exception((int)acquisition.captureCode, "PssCaptureSnapshot(threads only)");
                if (snapshot == IntPtr.Zero) throw new InvalidOperationException("PSS returned no snapshot handle.");
                acquisition.snapshotAcquired = true;
                PssThreadInformation information;
                acquisition.queryCode = PssQuerySnapshot(snapshot, 5, out information, (uint)Marshal.SizeOf(typeof(PssThreadInformation)));
                if (acquisition.queryCode != 0) throw new Win32Exception((int)acquisition.queryCode, "PssQuerySnapshot(thread information)");
                acquisition.capturedThreadCount = information.threadsCaptured;
                acquisition.contextLength = information.contextLength;
                if (information.threadsCaptured == 0)
                    throw new InvalidOperationException("PSS did not capture a nonempty thread snapshot.");
                acquisition.markerCreateCode = PssWalkMarkerCreate(IntPtr.Zero, out marker);
                if (acquisition.markerCreateCode != 0) throw new Win32Exception((int)acquisition.markerCreateCode, "PssWalkMarkerCreate");
                if (marker == IntPtr.Zero) throw new InvalidOperationException("PSS returned no walk marker.");
                acquisition.markerAcquired = true;
                while (true)
                {
                    PssThreadEntry entry;
                    var code = PssWalkSnapshot(snapshot, 3, marker, out entry, (uint)acquisition.entryBytes);
                    if (code != 0)
                    {
                        acquisition.walkEndCode = code;
                        if (code != 259) throw new Win32Exception((int)code, "PssWalkSnapshot(threads)");
                        break;
                    }
                    acquisition.walkedEntries++;
                    if (acquisition.walkedEntries > information.threadsCaptured || entry.processId != processId ||
                        entry.threadId == 0 || !unique.Add(entry.threadId))
                        throw new InvalidOperationException("PSS returned invalid, duplicate or foreign thread information.");
                    ids.Add(entry.threadId);
                }
                acquisition.currentThreadPresent = unique.Contains(currentThread);
                if (acquisition.walkedEntries != information.threadsCaptured || !acquisition.currentThreadPresent)
                    throw new InvalidOperationException("PSS thread enumeration is incomplete or omits the calling thread.");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                // HPSS/HPSSWALK are PSS descriptors, never CloseHandle values.
                if (acquisition.markerAcquired)
                    try { acquisition.markerFreeCode = PssWalkMarkerFree(marker); }
                    catch (Exception error) { failure = failure == null ? error : new InvalidOperationException(failure.ToString(), error); }
                if (acquisition.snapshotAcquired)
                    try { acquisition.snapshotFreeCode = PssFreeSnapshot(process, snapshot); }
                    catch (Exception error) { failure = failure == null ? error : new InvalidOperationException(failure.ToString(), error); }
                acquisition.released = (!acquisition.markerAcquired || acquisition.markerFreeCode == 0) &&
                    (!acquisition.snapshotAcquired || acquisition.snapshotFreeCode == 0);
                timer.Stop();
                acquisition.elapsedMilliseconds = timer.Elapsed.TotalMilliseconds;
            }
            if (failure != null || !acquisition.released)
            {
                acquisition.error = (failure == null ? "PSS cleanup failed." : failure.ToString()) +
                    " MarkerFree=" + acquisition.markerFreeCode + " SnapshotFree=" + acquisition.snapshotFreeCode;
                throw new InvalidOperationException(acquisition.error, failure);
            }
            acquisition.succeeded = true;
            ids.Sort();
            return ids.ToArray();
        }

        private static bool SameThreadIds(uint[] a, uint[] b)
        {
            if (a == null || b == null || a.Length == 0 || a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if (a[i] == 0 || a[i] != b[i] || i > 0 && a[i] <= a[i - 1]) return false;
            return true;
        }

        private static OwnedWindowSnapshot ReadOwnedWindow(IntPtr window, int processId, uint expectedThread)
        {
            uint owner;
            var thread = GetWindowThreadProcessId(window, out owner);
            var row = new OwnedWindowSnapshot {
                windowHandle = window.ToInt64(), processId = owner, nativeThreadId = thread,
                identityQuerySucceeded = thread != 0 && thread == expectedThread && owner == processId,
                identityQueryError = thread == 0 ? Marshal.GetLastWin32Error() : 0,
                visible = IsWindowVisible(window), minimized = IsIconic(window)
            };
            if (thread != 0)
            {
                var desktop = GetThreadDesktop(thread);
                row.desktopQueryError = desktop == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                if (desktop != IntPtr.Zero)
                {
                    try { row.desktop = Name(desktop); row.desktopQuerySucceeded = true; }
                    catch (Win32Exception error) { row.desktopQueryError = error.NativeErrorCode; }
                    row.inputQuery = ReadInputState(desktop);
                }
            }
            ClientRect rectangle;
            row.clientRectQuerySucceeded = GetClientRect(window, out rectangle);
            row.clientRectQueryError = row.clientRectQuerySucceeded ? 0 : Marshal.GetLastWin32Error();
            if (row.clientRectQuerySucceeded)
            {
                row.clientWidth = rectangle.right - rectangle.left;
                row.clientHeight = rectangle.bottom - rectangle.top;
            }
            return row;
        }

        private static void ReadOwnedProcessWindows(DesktopSnapshot snapshot)
        {
            var attempts = new List<ProcessUiScan>();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var scan = new ProcessUiScan { attempt = attempt + 1 };
                var timer = Stopwatch.StartNew();
                var windows = new List<OwnedWindowSnapshot>();
                var threads = new List<OwnedUiThreadSnapshot>();
                try
                {
                    scan.beforeThreadIds = CurrentProcessThreadIds(out scan.beforeThreadSnapshot);
                    foreach (var threadId in scan.beforeThreadIds)
                    {
                        var row = new OwnedUiThreadSnapshot { nativeThreadId = threadId };
                        SetLastError(0);
                        row.enumerationReturned = EnumThreadWindows(threadId, (window, parameter) => {
                            row.windowCount++;
                            try { windows.Add(ReadOwnedWindow(window, snapshot.processId, threadId)); }
                            catch (Exception error) { row.error = error.ToString(); }
                            // Never stop enumeration, including on a failed observation.
                            return true;
                        }, IntPtr.Zero);
                        row.errorCode = row.enumerationReturned ? 0 : Marshal.GetLastWin32Error();
                        // A zero-window thread can complete with TRUE; retain the
                        // documented FALSE/zero/error-free empty outcome as well.
                        row.querySucceeded = String.IsNullOrEmpty(row.error) && row.errorCode == 0 &&
                            (row.enumerationReturned || row.windowCount == 0);
                        threads.Add(row);
                    }
                    scan.afterThreadIds = CurrentProcessThreadIds(out scan.afterThreadSnapshot);
                    scan.cohortStable = SameThreadIds(scan.beforeThreadIds, scan.afterThreadIds);
                    scan.querySucceeded = true;
                }
                catch (Exception error) { scan.error = error.ToString(); }
                timer.Stop();
                scan.elapsedMilliseconds = timer.Elapsed.TotalMilliseconds;
                scan.threads = threads.ToArray();
                scan.windows = windows.ToArray();
                attempts.Add(scan);
                snapshot.processUiScanAttempts = attempts.ToArray();
                snapshot.processThreadQuerySucceeded = scan.querySucceeded;
                snapshot.processThreadCohortStable = scan.cohortStable;
                snapshot.processThreadIds = scan.beforeThreadIds;
                snapshot.processThreadIdsAfter = scan.afterThreadIds;
                snapshot.processThreadQueryError = scan.error;
                snapshot.ownedUiThreads = scan.threads;
                snapshot.ownedWindows = windows.ToArray();
                snapshot.ownedWindowsQuerySucceeded = scan.querySucceeded && scan.cohortStable;
                snapshot.ownedWindowsQueryError = 0;
                foreach (var thread in threads)
                    if (!thread.querySucceeded)
                    {
                        snapshot.ownedWindowsQuerySucceeded = false;
                        if (snapshot.ownedWindowsQueryError == 0) snapshot.ownedWindowsQueryError = thread.errorCode;
                    }
                // Only thread-cohort churn may trigger a new acquisition. A
                // failed or input-receiving owned-window observation is retained
                // and cannot be replaced by a later successful retry.
                if (!scan.querySucceeded) return;
                foreach (var thread in threads) if (!thread.querySucceeded) return;
                foreach (var window in windows)
                    if (!IsOwnedNonInputWindow(window, snapshot.runnerDesktop, snapshot.processId)) return;
                if (scan.querySucceeded && scan.cohortStable) return;
            }
        }

        private static bool IsNonInput(BooleanQuery query)
        {
            return query != null && query.succeeded && query.errorCode == 0 && query.bytesNeeded == 4 &&
                query.rawValue == 0 && query.value == false;
        }

        private static bool IsOwnedNonInputWindow(OwnedWindowSnapshot window, string expectedDesktop, int processId)
        {
            return window != null && window.windowHandle != 0 && window.identityQuerySucceeded && window.identityQueryError == 0 &&
                window.processId == processId && window.desktopQuerySucceeded && window.desktopQueryError == 0 &&
                String.Equals(window.desktop, expectedDesktop, StringComparison.OrdinalIgnoreCase) && IsNonInput(window.inputQuery) &&
                window.clientRectQuerySucceeded && window.clientRectQueryError == 0;
        }

        private static bool HasOwnedWindowCoverage(uint[] processThreads, OwnedUiThreadSnapshot[] observedThreads,
            OwnedWindowSnapshot[] observedWindows, string expectedDesktop, int processId, uint mainThreadId)
        {
            if (!SameThreadIds(processThreads, processThreads) || observedThreads == null || observedThreads.Length != processThreads.Length ||
                observedWindows == null || observedWindows.Length == 0) return false;
            var ids = new HashSet<uint>(processThreads);
            if (!ids.Contains(mainThreadId)) return false;
            var threadCounts = new Dictionary<uint, int>();
            foreach (var thread in observedThreads)
            {
                if (thread == null || !ids.Contains(thread.nativeThreadId) || threadCounts.ContainsKey(thread.nativeThreadId) ||
                    !thread.querySucceeded || thread.errorCode != 0 || !String.IsNullOrEmpty(thread.error) ||
                    thread.windowCount < 0 || !thread.enumerationReturned && thread.windowCount > 0) return false;
                threadCounts.Add(thread.nativeThreadId, thread.windowCount);
            }
            var windows = new HashSet<long>();
            var mainWindowObserved = false;
            foreach (var window in observedWindows)
            {
                if (!IsOwnedNonInputWindow(window, expectedDesktop, processId) || !windows.Add(window.windowHandle) ||
                    !threadCounts.ContainsKey(window.nativeThreadId)) return false;
                threadCounts[window.nativeThreadId]--;
                if (window.nativeThreadId == mainThreadId && window.clientWidth > 0 && window.clientHeight > 0) mainWindowObserved = true;
            }
            foreach (var count in threadCounts.Values) if (count != 0) return false;
            return mainWindowObserved;
        }

        // Pure receipt validation: never opens a desktop or queries the foreground.
        // The wrapper reuses this method when reading the saved observation history.
        public static bool HasNonInputOwnershipProof(DesktopSnapshot snapshot, string expectedDesktop, int processId, uint mainThreadId)
        {
            if (snapshot == null || String.IsNullOrEmpty(expectedDesktop) || processId <= 0 || mainThreadId == 0 ||
                !snapshot.currentDesktopQuerySucceeded || snapshot.currentDesktopQueryError != 0 ||
                !snapshot.windowStationQuerySucceeded || snapshot.windowStationQueryError != 0 ||
                String.IsNullOrEmpty(snapshot.windowStation) || snapshot.processId != processId || snapshot.nativeThreadId != mainThreadId ||
                !String.Equals(snapshot.runnerDesktop, expectedDesktop, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(snapshot.expectedDesktop, expectedDesktop, StringComparison.OrdinalIgnoreCase) ||
                !IsNonInput(snapshot.runnerInputQuery) || snapshot.runnerReceivesInput != false ||
                !snapshot.processThreadQuerySucceeded || !snapshot.processThreadCohortStable ||
                !String.IsNullOrEmpty(snapshot.processThreadQueryError) || !snapshot.ownedWindowsQuerySucceeded ||
                snapshot.ownedWindowsQueryError != 0 || !SameThreadIds(snapshot.processThreadIds, snapshot.processThreadIdsAfter) ||
                !HasOwnedWindowCoverage(snapshot.processThreadIds, snapshot.ownedUiThreads, snapshot.ownedWindows, expectedDesktop, processId, mainThreadId) ||
                snapshot.processUiScanAttempts == null || snapshot.processUiScanAttempts.Length == 0 || snapshot.processUiScanAttempts.Length > 3) return false;
            for (var index = 0; index < snapshot.processUiScanAttempts.Length; index++)
            {
                var scan = snapshot.processUiScanAttempts[index];
                if (scan == null || scan.attempt != index + 1 || !scan.querySucceeded || !String.IsNullOrEmpty(scan.error) ||
                    !SameThreadIds(scan.afterThreadIds, scan.afterThreadIds) ||
                    scan.cohortStable != SameThreadIds(scan.beforeThreadIds, scan.afterThreadIds) ||
                    !HasOwnedWindowCoverage(scan.beforeThreadIds, scan.threads, scan.windows, expectedDesktop, processId, mainThreadId)) return false;
            }
            var last = snapshot.processUiScanAttempts[snapshot.processUiScanAttempts.Length - 1];
            return last.cohortStable && SameThreadIds(last.beforeThreadIds, snapshot.processThreadIds) &&
                SameThreadIds(last.afterThreadIds, snapshot.processThreadIdsAfter);
        }

        public static ForegroundSnapshot ReadForeground()
        {
            var window = GetForegroundWindow();
            uint pid = 0;
            var thread = window == IntPtr.Zero ? 0 : GetWindowThreadProcessId(window, out pid);
            return new ForegroundSnapshot { capturedUtc = DateTime.UtcNow.ToString("O"),
                windowHandle = window.ToInt64(), processId = pid, nativeThreadId = thread };
        }

        private static string Quote(string argument)
        {
            if (argument == null || argument.IndexOf('\0') >= 0) throw new ArgumentException("Invalid process argument.");
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        internal static void ReleaseDesktop(IntPtr handle)
        {
            if (!CloseDesktop(handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "CloseDesktop(owned)");
        }

        public static DesktopLaunch StartBackground(string file, string directory, string[] arguments, string name)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name ?? "", "^UBGameTest_[a-f0-9]{32}$"))
                throw new ArgumentException("Expected an owned verification desktop name.");
            var command = new StringBuilder(Quote(file));
            foreach (var argument in arguments) command.Append(' ').Append(Quote(argument));
            if (command.Length >= 32767) throw new ArgumentException("The process command line is too long.");
            DesktopLaunch launch = null;
            Exception failure = null;
            IntPtr desktop = IntPtr.Zero;
            // Keep desktop creation and process launch on a short-lived thread;
            // STARTUPINFO.lpDesktop assigns the child's initial desktop.
            var creator = new Thread(() => {
                try
                {
                    var station = Name(RequireHandle(GetProcessWindowStation(), "GetProcessWindowStation"));
                    desktop = RequireHandle(CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0,
                        0x0001 | 0x0002, IntPtr.Zero), "CreateDesktopW");
                    // Keep the GPU window visible on the non-input desktop.
                    var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)),
                        desktop = station + "\\" + name, flags = 1, showWindow = 4 };
                    ProcessInfo info;
                    if (!CreateProcess(file, command, IntPtr.Zero, IntPtr.Zero, false, 0,
                        IntPtr.Zero, directory, ref startup, out info))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW(isolated game)");
                    try
                    {
                        var process = Process.GetProcessById((int)info.processId);
                        if (process.Handle == IntPtr.Zero) throw new InvalidOperationException("The owned process handle is unavailable.");
                        launch = new DesktopLaunch { Process = process, PrimaryThreadId = info.threadId,
                            DesktopName = name, WindowStationName = station, Handle = desktop };
                        desktop = IntPtr.Zero;
                    }
                    catch
                    {
                        TerminateProcess(info.process, 1);
                        WaitForSingleObject(info.process, 5000);
                        throw;
                    }
                    finally { CloseHandle(info.thread); CloseHandle(info.process); }
                }
                catch (Exception error) { failure = error; }
            });
            creator.Start();
            creator.Join();
            if (desktop != IntPtr.Zero) ReleaseDesktop(desktop);
            if (failure != null) throw new InvalidOperationException("The isolated game could not start.", failure);
            return launch;
        }
    }
}
