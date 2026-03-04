using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace RollingUpdateManager.Infrastructure
{
    /// <summary>
    /// Envuelve un Windows Job Object configurado con
    /// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: todos los procesos asignados
    /// al job son terminados automáticamente por el kernel cuando este
    /// objeto es destruido (es decir, cuando el manager cierra o crashea).
    ///
    /// Uso:
    ///   ProcessJobObject.Instance.Assign(process);
    ///
    /// Un único job cubre todos los procesos hijos — ningún java.exe
    /// queda huérfano aunque la app se cierre de forma abrupta.
    /// </summary>
    public sealed class ProcessJobObject : IDisposable
    {
        // ── Singleton ──────────────────────────────────────────────────────────
        public static readonly ProcessJobObject Instance = new();

        // ── P/Invoke ───────────────────────────────────────────────────────────
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob,
            JobObjectInfoType infoType,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
            int cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(
            ProcessAccessFlags dwDesiredAccess,
            bool bInheritHandle,
            int dwProcessId);

        // ── Constantes / estructuras ───────────────────────────────────────────
        private enum JobObjectInfoType { ExtendedLimitInformation = 9 }

        [Flags]
        private enum ProcessAccessFlags : uint { All = 0x001F0FFF }

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags, MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public IntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public IntPtr ProcessMemoryLimit, JobMemoryLimit;
            public IntPtr PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        // ── Estado interno ─────────────────────────────────────────────────────
        private readonly IntPtr _jobHandle;
        private bool _disposed;

        // Escribe en diag.log sin depender de ProcessLauncher (evita ciclo de dependencias)
        private static void DiagJob(string msg)
        {
            try
            {
                var file = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RollingUpdateManager", "diag.log");
                File.AppendAllText(file, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
            }
            catch { }
        }

        private ProcessJobObject()
        {
            _jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (_jobHandle == IntPtr.Zero)
            {
                DiagJob($"[JobObject] CreateJobObject FALLO: {Marshal.GetLastWin32Error()}");
                return;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

            bool ok = SetInformationJobObject(
                _jobHandle,
                JobObjectInfoType.ExtendedLimitInformation,
                ref info,
                Marshal.SizeOf(info));

            if (!ok)
                DiagJob($"[JobObject] SetInformation FALLO: {Marshal.GetLastWin32Error()}");
            else
                DiagJob("[JobObject] Job Object creado con KILL_ON_JOB_CLOSE OK");
        }

        /// <summary>
        /// Asigna el proceso al Job Object.
        /// Si falla (porque el proceso ya pertenece a otro job, p.ej. Visual Studio),
        /// lo registra en diag pero NO lanza excepción — el watchdog cubre este caso.
        /// </summary>
        public void Assign(Process process)
        {
            if (_jobHandle == IntPtr.Zero || _disposed) return;
            try
            {
                var hProcess = OpenProcess(ProcessAccessFlags.All, false, process.Id);
                if (hProcess == IntPtr.Zero)
                {
                    DiagJob($"[JobObject] OpenProcess PID={process.Id} FALLO: {Marshal.GetLastWin32Error()}");
                    return;
                }

                bool ok = AssignProcessToJobObject(_jobHandle, hProcess);
                CloseHandle(hProcess);

                if (!ok)
                    DiagJob($"[JobObject] Assign PID={process.Id} FALLO: {Marshal.GetLastWin32Error()} (job anidado — no critico)");
                else
                    DiagJob($"[JobObject] PID={process.Id} asignado al job OK");
            }
            catch (Exception ex)
            {
                DiagJob($"[JobObject] Assign excepcion: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_jobHandle != IntPtr.Zero)
                CloseHandle(_jobHandle);
        }
    }
}
