using Infrastructure.Extensions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Infrastructure
{
    public class ComputerHelper
    {
        /// <summary>
        /// 内存使用情况
        /// </summary>
        /// <returns></returns>
        public static MemoryMetrics GetComputerInfo()
        {
            try
            {
                MemoryMetricsClient client = new();
                MemoryMetrics memoryMetrics = IsUnix() ? client.GetUnixMetrics() : client.GetWindowsMetrics();

                memoryMetrics.FreeRam = Math.Round(memoryMetrics.Free / 1024, 2) + "GB";
                memoryMetrics.UsedRam = Math.Round(memoryMetrics.Used / 1024, 2) + "GB";
                memoryMetrics.TotalRAM = Math.Round(memoryMetrics.Total / 1024, 2) + "GB";
                memoryMetrics.RAMRate = memoryMetrics.Total > 0
                    ? Math.Ceiling(100 * memoryMetrics.Used / memoryMetrics.Total).ToString(CultureInfo.InvariantCulture) + "%"
                    : "0%";
                memoryMetrics.CPURate = Math.Ceiling(GetCPURate().ParseToDouble()).ToString(CultureInfo.InvariantCulture) + "%";
                return memoryMetrics;
            }
            catch (Exception ex)
            {
                Console.WriteLine("获取内存使用出错，msg=" + ex.Message + "," + ex.StackTrace);
            }
            return new MemoryMetrics();
        }

        /// <summary>
        /// 获取内存大小
        /// </summary>
        /// <returns></returns>
        public static List<DiskInfo> GetDiskInfos()
        {
            List<DiskInfo> diskInfos = new();

            if (IsUnix())
            {
                try
                {
                    string output = ShellHelper.Bash("df -m / | awk '{print $2,$3,$4,$5,$6}'");
                    var arr = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (arr.Length == 0) return diskInfos;

                    var rootDisk = arr[1].Split(' ', (char)StringSplitOptions.RemoveEmptyEntries);
                    if (rootDisk == null || rootDisk.Length == 0)
                    {
                        return diskInfos;
                    }
                    DiskInfo diskInfo = new()
                    {
                        DiskName = "/",
                        TotalSize = long.Parse(rootDisk[0]) / 1024,
                        Used = long.Parse(rootDisk[1]) / 1024,
                        AvailableFreeSpace = long.Parse(rootDisk[2]) / 1024,
                        AvailablePercent = decimal.Parse(rootDisk[3].Replace("%", ""))
                    };
                    diskInfos.Add(diskInfo);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("获取磁盘信息出错了" + ex.Message);
                }
            }
            else
            {
                var driv = DriveInfo.GetDrives();
                foreach (var item in driv)
                {
                    try
                    {
                        var obj = new DiskInfo()
                        {
                            DiskName = item.Name,
                            TypeName = item.DriveType.ToString(),
                            TotalSize = item.TotalSize / 1024 / 1024 / 1024,
                            AvailableFreeSpace = item.AvailableFreeSpace / 1024 / 1024 / 1024,
                        };
                        obj.Used = obj.TotalSize - obj.AvailableFreeSpace;
                        obj.AvailablePercent = decimal.Ceiling(obj.Used / (decimal)obj.TotalSize * 100);
                        diskInfos.Add(obj);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("获取磁盘信息出错了" + ex.Message);
                        continue;
                    }
                }
            }

            return diskInfos;
        }

        public static bool IsUnix()
        {
            var isUnix = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
            return isUnix;
        }

        public static string GetCPURate()
        {
            string cpuRate;
            if (IsUnix())
            {
                string output = ShellHelper.Bash("top -b -n1 | grep \"Cpu(s)\" | awk '{print $2 + $4}'");
                cpuRate = output.Trim();
            }
            else
            {
                // wmic 已从新版 Windows（Win11 24H2+/Server 2025）默认移除，改用 GetSystemTimes 两次采样计算
                cpuRate = GetWindowsCpuRate();
            }
            return cpuRate;
        }

        /// <summary>
        /// Windows 下计算 CPU 使用率：GetSystemTimes 两次采样取差值（内核时间已含空闲时间）。
        /// </summary>
        private static string GetWindowsCpuRate()
        {
            if (!Win32Native.GetSystemTimes(out var idle1, out var kernel1, out var user1))
            {
                return "0";
            }

            // 500ms 采样间隔：与原 wmic 调用耗时相当，精度足够
            Thread.Sleep(500);

            if (!Win32Native.GetSystemTimes(out var idle2, out var kernel2, out var user2))
            {
                return "0";
            }

            var idleDelta = Win32Native.ToUInt64(idle2) - Win32Native.ToUInt64(idle1);
            var totalDelta = (Win32Native.ToUInt64(kernel2) - Win32Native.ToUInt64(kernel1))
                             + (Win32Native.ToUInt64(user2) - Win32Native.ToUInt64(user1));
            if (totalDelta <= 0)
            {
                return "0";
            }

            var usage = Math.Round(100d * (totalDelta - idleDelta) / totalDelta, 2);
            return Math.Clamp(usage, 0, 100).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 获取系统运行时间
        /// </summary>
        /// <returns></returns>
        public static string GetRunTime()
        {
            string runTime = string.Empty;
            try
            {
                if (IsUnix())
                {
                    var unixRunTime = GetUnixRunTime();
                    if (unixRunTime.HasValue)
                    {
                        runTime = DateTimeHelper.FormatTime(((long)unixRunTime.Value.TotalMilliseconds).ToString(CultureInfo.InvariantCulture).ParseToLong());
                    }
                    else
                    {
                        string output = ShellHelper.Bash("uptime -s").Trim();
                        runTime = DateTimeHelper.FormatTime((DateTime.Now - output.ParseToDateTime()).TotalMilliseconds.ToString().Split('.')[0].ParseToLong());
                    }
                }
                else
                {
                    // wmic 已从新版 Windows（Win11 24H2+/Server 2025）默认移除；
                    // Environment.TickCount64 即系统启动以来的毫秒数（64 位，无 49 天溢出问题）
                    runTime = DateTimeHelper.FormatTime(Environment.TickCount64.ToString(CultureInfo.InvariantCulture).ParseToLong());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("获取runTime出错" + ex.Message);
            }
            return runTime;
        }

        private static TimeSpan? GetUnixRunTime()
        {
            try
            {
                using var initProcess = Process.GetProcessById(1);
                var startTime = initProcess.StartTime;
                var now = DateTime.Now;
                if (startTime <= now)
                {
                    return now - startTime;
                }
            }
            catch
            {
            }

            try
            {
                if (File.Exists("/proc/uptime"))
                {
                    var content = File.ReadAllText("/proc/uptime").Trim();
                    var secondsText = content.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (double.TryParse(secondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                    {
                        return TimeSpan.FromSeconds(seconds);
                    }
                }
            }
            catch
            {
            }

            return null;
        }
    }

    /// <summary>
    /// 内存信息
    /// </summary>
    public class MemoryMetrics
    {
        [JsonIgnore]
        public double Total { get; set; }
        [JsonIgnore]
        public double Used { get; set; }
        [JsonIgnore]
        public double Free { get; set; }

        public string UsedRam { get; set; }
        /// <summary>
        /// CPU使用率%
        /// </summary>
        public string CPURate { get; set; }
        /// <summary>
        /// 总内存 GB
        /// </summary>
        public string TotalRAM { get; set; }
        /// <summary>
        /// 内存使用率 %
        /// </summary>
        public string RAMRate { get; set; }
        /// <summary>
        /// 空闲内存
        /// </summary>
        public string FreeRam { get; set; }
    }

    public class DiskInfo
    {
        /// <summary>
        /// 磁盘名
        /// </summary>
        public string DiskName { get; set; }
        public string TypeName { get; set; }
        public long TotalFree { get; set; }
        public long TotalSize { get; set; }
        /// <summary>
        /// 已使用
        /// </summary>
        public long Used { get; set; }
        /// <summary>
        /// 可使用
        /// </summary>
        public long AvailableFreeSpace { get; set; }
        public decimal AvailablePercent { get; set; }
    }

    public class MemoryMetricsClient
    {
        #region 获取内存信息

        private static readonly string[] CGroupV2LimitPaths =
        {
            "/sys/fs/cgroup/memory.max"
        };

        private static readonly string[] CGroupV2UsagePaths =
        {
            "/sys/fs/cgroup/memory.current"
        };

        private static readonly string[] CGroupV1LimitPaths =
        {
            "/sys/fs/cgroup/memory/memory.limit_in_bytes"
        };

        private static readonly string[] CGroupV1UsagePaths =
        {
            "/sys/fs/cgroup/memory/memory.usage_in_bytes"
        };

        /// <summary>
        /// windows系统获取内存信息
        /// </summary>
        /// <returns></returns>
        public MemoryMetrics GetWindowsMetrics()
        {
            var metrics = new MemoryMetrics();

            // wmic 已从新版 Windows（Win11 24H2+/Server 2025）默认移除：改用 GlobalMemoryStatusEx 读取物理内存。
            // 单位与原 wmic 口径一致：MB（wmic 返回 KB/1024）
            var status = new Win32Native.MemoryStatusEx();
            status.dwLength = (uint)Marshal.SizeOf<Win32Native.MemoryStatusEx>();
            if (!Win32Native.GlobalMemoryStatusEx(ref status))
            {
                return metrics;
            }

            metrics.Total = Math.Round(status.ullTotalPhys / 1024d / 1024d, 0);
            metrics.Free = Math.Round(status.ullAvailPhys / 1024d / 1024d, 0);
            metrics.Used = metrics.Total - metrics.Free;

            return metrics;
        }

        /// <summary>
        /// Unix系统获取
        /// </summary>
        /// <returns></returns>
        public MemoryMetrics GetUnixMetrics()
        {
            var metrics = GetUnixMetricsFromCGroup();
            if (metrics != null)
            {
                return metrics;
            }

            string output = ShellHelper.Bash("free -m");
            metrics = new MemoryMetrics();
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length <= 0) return metrics;

            var memLine = lines.FirstOrDefault(x => x.TrimStart().StartsWith("Mem:", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(memLine))
            {
                var memory = memLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (memory.Length >= 4)
                {
                    if (double.TryParse(memory[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var total))
                    {
                        metrics.Total = total;
                    }

                    if (double.TryParse(memory[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var used))
                    {
                        metrics.Used = used;
                    }

                    if (double.TryParse(memory[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var free))
                    {
                        metrics.Free = free;
                    }

                    if (metrics.Total > 0 && metrics.Used <= 0 && metrics.Free > 0)
                    {
                        metrics.Used = Math.Max(metrics.Total - metrics.Free, 0);
                    }

                    return metrics;
                }
            }

            var firstDataLine = lines.FirstOrDefault(x =>
            {
                var cols = x.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return cols.Length >= 3 &&
                       double.TryParse(cols[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            });

            if (!string.IsNullOrWhiteSpace(firstDataLine))
            {
                var memory = firstDataLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (memory.Length >= 3)
                {
                    if (double.TryParse(memory[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var total))
                    {
                        metrics.Total = total;
                    }

                    if (double.TryParse(memory[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var used))
                    {
                        metrics.Used = used;
                    }

                    if (double.TryParse(memory[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var free))
                    {
                        metrics.Free = free;
                    }
                }
            }

            return metrics;
        }

        private static MemoryMetrics GetUnixMetricsFromCGroup()
        {
            long totalBytes = TryReadCGroupBytes(CGroupV2LimitPaths);
            long usedBytes = TryReadCGroupBytes(CGroupV2UsagePaths);

            if (totalBytes <= 0 || usedBytes < 0)
            {
                totalBytes = TryReadCGroupBytes(CGroupV1LimitPaths);
                usedBytes = TryReadCGroupBytes(CGroupV1UsagePaths);
            }

            if (!IsValidCGroupLimit(totalBytes) || usedBytes < 0)
            {
                return null;
            }

            if (usedBytes > totalBytes)
            {
                usedBytes = totalBytes;
            }

            var totalMb = totalBytes / 1024d / 1024d;
            var usedMb = usedBytes / 1024d / 1024d;
            var freeMb = Math.Max(totalMb - usedMb, 0d);

            return new MemoryMetrics
            {
                Total = Math.Round(totalMb, 0),
                Used = Math.Round(usedMb, 0),
                Free = Math.Round(freeMb, 0)
            };
        }

        private static long TryReadCGroupBytes(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    var text = File.ReadAllText(path).Trim();
                    if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "max", StringComparison.OrdinalIgnoreCase))
                    {
                        return -1;
                    }

                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    {
                        return value;
                    }
                }
                catch
                {
                }
            }

            return -1;
        }

        private static bool IsValidCGroupLimit(long totalBytes)
        {
            if (totalBytes <= 0)
            {
                return false;
            }

            const long onePb = 1024L * 1024L * 1024L * 1024L * 1024L;
            return totalBytes < onePb;
        }
        #endregion
    }

    /// <summary>
    /// Windows 原生 API 封装：替代已从新版 Windows（Win11 24H2+/Server 2025）默认移除的 wmic 命令行采集。
    /// 仅允许在 Windows 分支调用。
    /// </summary>
    internal static class Win32Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileTime
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetSystemTimes(out FileTime lpIdleTime, out FileTime lpKernelTime, out FileTime lpUserTime);

        internal static ulong ToUInt64(in FileTime value) => ((ulong)value.dwHighDateTime << 32) | value.dwLowDateTime;
    }
}