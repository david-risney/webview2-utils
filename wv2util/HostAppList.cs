﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace wv2util
{
    public class HostAppEntry : IEquatable<HostAppEntry>
    {
        public HostAppEntry(string exePath, string runtimePath, string userDataPath)
        {
            ExecutablePath = exePath;
            Runtime = new RuntimeEntry(runtimePath);
            UserDataPath = userDataPath;
        }
        public string ExecutableName
        { 
            get
            {
                return ExecutablePath.Split(new char[] { '\\', '/' }).ToList<string>().Last<string>();
            }
        }
        public string ExecutablePath { get; private set; }
        public RuntimeEntry Runtime { get; private set; }
        public string UserDataPath { get; private set; }

        public bool Equals(HostAppEntry other)
        {
            return ExecutablePath == other.ExecutablePath &&
                UserDataPath == other.UserDataPath &&
                Runtime.Equals(other.Runtime);
        }
    }

    public class HostAppList : ObservableCollection<HostAppEntry>
    {
        public HostAppList()
        {
            FromMachineAsync();
        }

        // This is clearly not thread safe. It assumes FromDiskAsync will only
        // be called from the same thread.
        public async Task FromMachineAsync()
        {
            if (m_fromMachineInProgress != null)
            {
                await m_fromMachineInProgress;
            }
            else
            {
                m_fromMachineInProgress = FromMachineInnerAsync();
                await m_fromMachineInProgress;
                m_fromMachineInProgress = null;
            }
        }
        protected Task m_fromMachineInProgress = null;
        protected async Task FromMachineInnerAsync()
        {

            IEnumerable<HostAppEntry> newEntries = null;

            await Task.Factory.StartNew(() =>
            {
                ProcessSnapshotHelper.ReloadSnapshot();
                newEntries = GetHostAppEntriesFromMachine().ToList<HostAppEntry>();
            });

            // Only update the entries on the caller thread to ensure the
            // caller isn't trying to enumerate the entries while
            // we're updating them.
            SetEntries(newEntries);
        }

        private void SetEntries(IEnumerable<HostAppEntry> newEntries)
        {
            // Use ToList to get a fixed collection that won't get angry that we're calling
            // Add and Remove on it while enumerating.
            foreach (var entry in this.Except(newEntries).ToList<HostAppEntry>())
            {
                this.Items.Remove(entry);
            }
            foreach (var entry in newEntries.Except(this).ToList<HostAppEntry>())
            {
                this.Items.Add(entry);
            }
            this.OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            this.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            this.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        private static IEnumerable<HostAppEntry> GetHostAppEntriesFromMachine()
        {
            var processes = Process.GetProcessesByName("msedgewebview2");
            foreach (var process in processes)
            {
                var commandLineParts = process.GetCommandLine().Split(' ');
                string processType = null;
                string userDataPath = null;
                foreach (var commandLinePart in commandLineParts)
                {
                    string commandLinePartTrimmed = commandLinePart.Trim().Replace("\\\"", "\"").Trim('"');
                    if (commandLinePartTrimmed.StartsWith("--type"))
                    {
                        processType = commandLinePartTrimmed.Split('=')[1].Trim(new char[] { '"', ' '});
                    }

                    if (commandLinePartTrimmed.StartsWith("--user-data-dir"))
                    {
                        userDataPath = commandLinePartTrimmed.Split('=')[1].Trim(new char[] { '"', ' ' });
                    }
                }

                if (processType == null)
                {
                    HostAppEntry entry = new HostAppEntry(
                        process.ParentProcess().GetMainModuleFileName(),
                        process.GetMainModuleFileName(),
                        userDataPath);
                    yield return entry;
                }
            }
        }
    }

    public static class ProcessExtensions
    {
        private static string FindIndexedProcessName(int pid)
        {
            var processName = Process.GetProcessById(pid).ProcessName;
            var processesByName = Process.GetProcessesByName(processName);
            string processIndexdName = null;

            for (var index = 0; index < processesByName.Length; index++)
            {
                processIndexdName = index == 0 ? processName : processName + "#" + index;
                var processId = new PerformanceCounter("Process", "ID Process", processIndexdName);
                if ((int)processId.NextValue() == pid)
                {
                    return processIndexdName;
                }
            }

            return processIndexdName;
        }

        private static Process FindPidFromIndexedProcessName(string indexedProcessName)
        {
            var parentId = new PerformanceCounter("Process", "Creating Process ID", indexedProcessName);
            return Process.GetProcessById((int)parentId.NextValue());
        }

        public static Process Parent(this Process process)
        {
            return FindPidFromIndexedProcessName(FindIndexedProcessName(process.Id));
        }

        public static string GetCommandLine(this Process process)
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + process.Id))
            using (ManagementObjectCollection objects = searcher.Get())
            {
                return objects.Cast<ManagementBaseObject>().SingleOrDefault()?["CommandLine"]?.ToString();
            }

        }

        [DllImport("Kernel32.dll")]
        private static extern bool QueryFullProcessImageName([In] IntPtr hProcess, [In] uint dwFlags, [Out] StringBuilder lpExeName, [In, Out] ref uint lpdwSize);

        public static string GetMainModuleFileName(this Process process, int buffer = 1024)
        {
            var fileNameBuilder = new StringBuilder(buffer);
            uint bufferLength = (uint)fileNameBuilder.Capacity + 1;
            return QueryFullProcessImageName(process.Handle, 0, fileNameBuilder, ref bufferLength) ?
                fileNameBuilder.ToString() :
                null;
        }
    }

    public static class ProcessSnapshotHelper
    {
        private static ProcessSnapshot s_snapShot = new ProcessSnapshot();
        public static void ReloadSnapshot()
        {
            s_snapShot.Reload();
        }

        public static Process ParentProcess(this Process process)
        {
            return s_snapShot.GetParentProcess(process);
        }
    }

    public class ProcessSnapshot
    {
        private static readonly uint TH32CS_SNAPPROCESS = 2;
        private IntPtr m_hSnapshot = IntPtr.Zero;
        public void Reload()
        {
            if (m_hSnapshot != IntPtr.Zero)
            {
                CloseHandle(m_hSnapshot);
            }
            m_hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        }
        private void EnsureSnapshot()
        {
            if (m_hSnapshot == IntPtr.Zero)
            {
                Reload();
            }
        }

        /// <summary>
        /// Returns the Parent Process of a Process
        /// </summary>
        /// <param name="process">The Windows Process.</param>
        /// <returns>The Parent Process of the Process.</returns>
        public Process GetParentProcess(Process childProcess)
        {
            int parentPid = 0;
            int processPid = childProcess.Id;

            EnsureSnapshot();

            PROCESSENTRY32 procInfo = new PROCESSENTRY32();

            procInfo.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

            // Read first
            if (Process32First(m_hSnapshot, ref procInfo) == false)
            {
                return null;
            }

            // Loop through the snapshot
            do
            {
                // If it's me, then ask for my parent.
                if (processPid == procInfo.th32ProcessID)
                {
                    parentPid = (int)procInfo.th32ParentProcessID;
                }
            }
            while (parentPid == 0 && Process32Next(m_hSnapshot, ref procInfo)); // Read next

            if (parentPid > 0)
            {
                return Process.GetProcessById(parentPid);
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Takes a snapshot of the specified processes, as well as the heaps, 
        /// modules, and threads used by these processes.
        /// </summary>
        /// <param name="dwFlags">
        /// The portions of the system to be included in the snapshot.
        /// </param>
        /// <param name="th32ProcessID">
        /// The process identifier of the process to be included in the snapshot.
        /// </param>
        /// <returns>
        /// If the function succeeds, it returns an open handle to the specified snapshot.
        /// If the function fails, it returns INVALID_HANDLE_VALUE.
        /// </returns>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        /// <summary>
        /// Retrieves information about the first process encountered in a system snapshot.
        /// </summary>
        /// <param name="hSnapshot">A handle to the snapshot.</param>
        /// <param name="lppe">A pointer to a PROCESSENTRY32 structure.</param>
        /// <returns>
        /// Returns TRUE if the first entry of the process list has been copied to the buffer.
        /// Returns FALSE otherwise.
        /// </returns>
        [DllImport("kernel32.dll")]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        /// <summary>
        /// Retrieves information about the next process recorded in a system snapshot.
        /// </summary>
        /// <param name="hSnapshot">A handle to the snapshot.</param>
        /// <param name="lppe">A pointer to a PROCESSENTRY32 structure.</param>
        /// <returns>
        /// Returns TRUE if the next entry of the process list has been copied to the buffer.
        /// Returns FALSE otherwise.</returns>
        [DllImport("kernel32.dll")]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hSnapshot);

        /// <summary>
        /// Describes an entry from a list of the processes residing 
        /// in the system address space when a snapshot was taken.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }
    }
}
