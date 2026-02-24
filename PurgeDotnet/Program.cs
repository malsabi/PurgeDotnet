using System;
using System.Linq;
using System.Management;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;

namespace PurgeDotnet;

public static class Program
{
    #region Constants
    private const string DotnetProcessName = "dotnet";
    private const int HungResponseTimeoutMs = 5000;
    #endregion

    public static void Main(string[] _)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("═══════════════════════════════════════════════");
        Console.WriteLine("        Dotnet Process Purger v1.0");
        Console.WriteLine("═══════════════════════════════════════════════");
        Console.ResetColor();
        Console.WriteLine();

        List<OrphanedProcessInfo> orphanedProcesses = FindOrphanedDotnetProcesses();

        if (orphanedProcesses.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("No orphaned/stuck dotnet.exe processes found. System is clean.");
            Console.ResetColor();
            return;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Found {orphanedProcesses.Count} orphaned/stuck dotnet.exe process(es):");
        Console.ResetColor();
        Console.WriteLine();

        foreach (OrphanedProcessInfo info in orphanedProcesses)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write($"  PID: {info.ProcessId,-8}");
            Console.ResetColor();
            Console.Write($" | Memory: {info.MemoryMb,7:F1} MB");
            Console.Write($" | Running: {info.RunningTime}");
            Console.Write($" | Responding: {(info.IsResponding ? "Yes" : "No")}");

            if (info.ChildCount > 0)
                Console.Write($" | Children: {info.ChildCount}");

            Console.WriteLine();

            if (!string.IsNullOrEmpty(info.CommandLine))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"           Cmd: {Truncate(info.CommandLine, 80)}");
                Console.ResetColor();
            }
        }

        double totalMemoryMb = orphanedProcesses.Sum(p => p.MemoryMb);
        int totalChildren = orphanedProcesses.Sum(p => p.ChildCount);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Total memory consumed: {totalMemoryMb:F1} MB");

        if (totalChildren > 0)
            Console.WriteLine($"Total child processes: {totalChildren}");

        Console.ResetColor();
        Console.WriteLine();

        Console.Write("Do you want to purge all these processes (including children)? [y/N]: ");
        string input = Console.ReadLine()?.Trim() ?? string.Empty;

        if (string.Equals(input, "y", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "yes", StringComparison.OrdinalIgnoreCase))
        {
            PurgeProcesses(orphanedProcesses);
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("Purge cancelled. No processes were killed.");
            Console.ResetColor();
        }
    }

    #region Private Methods
    private static List<OrphanedProcessInfo> FindOrphanedDotnetProcesses()
    {
        List<OrphanedProcessInfo> orphaned = [];
        int currentPid = Environment.ProcessId;
        Process[] dotnetProcesses = Process.GetProcessesByName(DotnetProcessName);

        foreach (Process process in dotnetProcesses)
        {
            try
            {
                if (process.Id == currentPid)
                    continue;

                int parentPid = GetParentProcessId(process.Id);
                bool isOrphaned = IsOrphanedProcess(parentPid);
                bool isResponding = IsProcessResponding(process);
                bool isStuck = !isResponding || IsLikelyStuck(process);

                if (!isOrphaned && !isStuck)
                    continue;

                List<int> childPids = GetChildProcessIds(process.Id);

                OrphanedProcessInfo info = new()
                {
                    ProcessId = process.Id,
                    MemoryMb = process.WorkingSet64 / (1024.0 * 1024.0),
                    RunningTime = FormatTimeSpan(DateTime.Now - process.StartTime),
                    IsResponding = isResponding,
                    CommandLine = GetCommandLine(process.Id),
                    ChildProcessIds = childPids,
                    ChildCount = childPids.Count
                };

                orphaned.Add(info);
            }
            catch (Exception)
            {
                // Process may have exited or access denied — skip it
            }
            finally
            {
                process.Dispose();
            }
        }

        return orphaned;
    }

    private static bool IsOrphanedProcess(int parentPid)
    {
        if (parentPid <= 0)
            return true;

        try
        {
            Process parent = Process.GetProcessById(parentPid);
            parent.Dispose();
            return false;
        }
        catch (ArgumentException)
        {
            // Parent process no longer exists — this process is orphaned
            return true;
        }
    }

    private static bool IsProcessResponding(Process process)
    {
        try
        {
            // For console apps, MainWindowHandle will be IntPtr.Zero.
            // We check if the process has consumed CPU recently as a proxy.
            if (process.MainWindowHandle != IntPtr.Zero)
                return process.Responding;

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsLikelyStuck(Process process)
    {
        try
        {
            // A dotnet process with no parent that has been idle (very low CPU)
            // for a significant time is likely stuck
            TimeSpan totalCpu = process.TotalProcessorTime;
            TimeSpan runningTime = DateTime.Now - process.StartTime;

            // If running for more than 5 minutes with near-zero CPU usage, likely stuck
            if (runningTime.TotalMinutes > 5 && totalCpu.TotalSeconds < 1)
                return true;

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int GetParentProcessId(int processId)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return GetParentProcessIdWindows(processId);

        return GetParentProcessIdUnix(processId);
    }

    [SupportedOSPlatform("windows")]
    private static int GetParentProcessIdWindows(int processId)
    {
        try
        {
            using ManagementObject managementObject = new($"win32_process.handle='{processId}'");
            managementObject.Get();
            return Convert.ToInt32(managementObject["ParentProcessId"]);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static int GetParentProcessIdUnix(int processId)
    {
        try
        {
            string statusPath = $"/proc/{processId}/status";

            if (!System.IO.File.Exists(statusPath))
                return -1;

            string[] lines = System.IO.File.ReadAllLines(statusPath);
            string ppidLine = lines.FirstOrDefault(l => l.StartsWith("PPid:", StringComparison.Ordinal)) ?? string.Empty;

            if (string.IsNullOrEmpty(ppidLine))
                return -1;

            string ppidValue = ppidLine.Split('\t', StringSplitOptions.RemoveEmptyEntries).Last().Trim();
            return int.Parse(ppidValue);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static List<int> GetChildProcessIds(int parentId)
    {
        List<int> children = [];

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            GetChildProcessIdsWindows(parentId, children);
        }
        else
        {
            GetChildProcessIdsUnix(parentId, children);
        }

        return children;
    }

    [SupportedOSPlatform("windows")]
    private static void GetChildProcessIdsWindows(int parentId, List<int> children)
    {
        try
        {
            string query = $"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = {parentId}";
            using ManagementObjectSearcher searcher = new(query);
            using ManagementObjectCollection results = searcher.Get();

            foreach (ManagementBaseObject obj in results)
            {
                int childPid = Convert.ToInt32(obj["ProcessId"]);
                children.Add(childPid);
                // Recursively find grandchildren
                GetChildProcessIdsWindows(childPid, children);
            }
        }
        catch (Exception)
        {
            // Access denied or WMI error — skip
        }
    }

    private static void GetChildProcessIdsUnix(int parentId, List<int> children)
    {
        try
        {
            ProcessStartInfo startInfo = new("pgrep", $"-P {parentId}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process? pgrep = Process.Start(startInfo);

            if (pgrep == null)
                return;

            string output = pgrep.StandardOutput.ReadToEnd();
            pgrep.WaitForExit();

            string[] pids = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (string pidStr in pids)
            {
                if (int.TryParse(pidStr.Trim(), out int childPid))
                {
                    children.Add(childPid);
                    // Recursively find grandchildren
                    GetChildProcessIdsUnix(childPid, children);
                }
            }
        }
        catch (Exception)
        {
            // pgrep not available or error — skip
        }
    }

    private static string GetCommandLine(int processId)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return GetCommandLineWindows(processId);

        return GetCommandLineUnix(processId);
    }

    [SupportedOSPlatform("windows")]
    private static string GetCommandLineWindows(int processId)
    {
        try
        {
            string query = $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}";
            using ManagementObjectSearcher searcher = new(query);
            using ManagementObjectCollection results = searcher.Get();

            foreach (ManagementBaseObject obj in results)
            {
                return obj["CommandLine"]?.ToString() ?? string.Empty;
            }
        }
        catch (Exception)
        {
            // WMI access error — skip
        }

        return string.Empty;
    }

    private static string GetCommandLineUnix(int processId)
    {
        try
        {
            string cmdlinePath = $"/proc/{processId}/cmdline";

            if (!System.IO.File.Exists(cmdlinePath))
                return string.Empty;

            string raw = System.IO.File.ReadAllText(cmdlinePath);
            return raw.Replace('\0', ' ').Trim();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static void PurgeProcesses(List<OrphanedProcessInfo> orphanedProcesses)
    {
        Console.WriteLine();
        int killed = 0;
        int failed = 0;

        foreach (OrphanedProcessInfo info in orphanedProcesses)
        {
            // Kill children first (deepest first since list is built recursively)
            for (int i = info.ChildProcessIds.Count - 1; i >= 0; i--)
            {
                int childPid = info.ChildProcessIds[i];
                KillProcess(childPid, isChild: true, ref killed, ref failed);
            }

            // Kill the parent process
            KillProcess(info.ProcessId, isChild: false, ref killed, ref failed);
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Purge complete. Killed: {killed} | Failed: {failed}");
        Console.ResetColor();
    }

    private static void KillProcess(int processId, bool isChild, ref int killed, ref int failed)
    {
        try
        {
            Process process = Process.GetProcessById(processId);
            string label = isChild ? "  Child" : "  Process";
            Console.Write($"{label} PID {processId}... ");

            process.Kill(entireProcessTree: true);
            process.WaitForExit(HungResponseTimeoutMs);
            process.Dispose();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("killed");
            Console.ResetColor();
            killed++;
        }
        catch (InvalidOperationException)
        {
            // Process already exited
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("already exited");
            Console.ResetColor();
            killed++;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"failed ({ex.Message})");
            Console.ResetColor();
            failed++;
        }
    }

    private static string FormatTimeSpan(TimeSpan span)
    {
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}d {span.Hours}h";

        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m";

        return $"{(int)span.TotalMinutes}m {span.Seconds}s";
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (value.Length <= maxLength)
            return value;

        return value[..(maxLength - 3)] + "...";
    }
    #endregion

    private sealed class OrphanedProcessInfo
    {
        #region Properties
        public int ProcessId { get; set; }
        public double MemoryMb { get; set; }
        public string RunningTime { get; set; } = string.Empty;
        public bool IsResponding { get; set; }
        public string CommandLine { get; set; } = string.Empty;
        public List<int> ChildProcessIds { get; set; } = [];
        public int ChildCount { get; set; }
        #endregion
    }
}