﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace wv2util
{    
    public class RuntimeEntry : IEquatable<RuntimeEntry>
    {
        public RuntimeEntry(string location)
        {
            ExePath = location;
        }
        public string ExePath { get; protected set; }
        public string RuntimeLocation { get { return Directory.GetParent(ExePath).FullName; } }
        public string Version
        {
            get
            {
                try
                {
                    return FileVersionInfo.GetVersionInfo(ExePath).FileVersion;
                }
                catch (System.IO.FileNotFoundException)
                {
                    // Somehow this is possible.
                    return "File not found";
                }
            }
        }
        public string Channel
        { 
            get
            {
                if (ExePath.ToLower().Contains("\\edge sxs\\"))
                {
                    return "Canary";
                }
                else if (ExePath.ToLower().Contains("\\edge beta\\"))
                {
                    return "Beta";
                }
                else if (ExePath.ToLower().Contains("\\edge dev\\"))
                {
                    return "Dev";
                }
                else if (ExePath.ToLower().Contains("\\edge\\"))
                {
                    return "Stable";
                }
                else
                {
                    return "Unknown";
                }
            }
        }

        public bool Equals(RuntimeEntry other)
        {
            return RuntimeLocation == other.RuntimeLocation;
        }
    }

    public class RuntimeList : ObservableCollection<RuntimeEntry>
    {
        public RuntimeList()
        {
            FromDiskAsync();
        }

        private Task m_inProgressFromDisk = null;

        // This is clearly not thread safe. It assumes FromDiskAsync will only
        // be called from the same thread.
        public async Task FromDiskAsync()
        {
            if (m_inProgressFromDisk != null)
            {
                await m_inProgressFromDisk;
            }
            else
            {
                m_inProgressFromDisk = FromDiskInnerAsync();
                await m_inProgressFromDisk;
                m_inProgressFromDisk = null;
            }
        }

        private async Task FromDiskInnerAsync()
        {
            IEnumerable<RuntimeEntry> newEntries = null;
            await Task.Factory.StartNew(() =>
            {
                newEntries = RuntimeList.GetInstalledRuntimes();
            });
            // Only update the entries on the caller thread to ensure the
            // caller isn't trying to enumerate the entries while
            // we're updating them.
            this.SetEntries(newEntries);
        }

        protected void SetEntries(IEnumerable<RuntimeEntry> newEntries)
        { 
            // Use ToList to get a fixed collection that won't get angry that we're calling
            // Add and Remove on it while enumerating.
            foreach (var entry in this.Except(newEntries).ToList<RuntimeEntry>())
            {
                this.Items.Remove(entry);
            }
            foreach (var entry in newEntries.Except(this).ToList<RuntimeEntry>())
            {
                this.Items.Add(entry);
            }

            this.OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            this.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            this.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
        private static IEnumerable<RuntimeEntry> GetInstalledRuntimes()
        {
            string[] rootPaths =
            {
                Environment.GetEnvironmentVariable("LOCALAPPDATA") + "\\Microsoft\\",
                Environment.GetEnvironmentVariable("ProgramFiles(x86)") + "\\Microsoft\\",
                Environment.GetEnvironmentVariable("ProgramFiles") + "\\Microsoft\\"
            };

            foreach (string rootPath in rootPaths)
            {
                IEnumerable<string> potentialParents = null;
                try
                {
                    potentialParents = Directory.GetDirectories(rootPath).Where(path => path.Contains("Edge"));
                }
                catch (System.IO.DirectoryNotFoundException e)
                {
                    Debug.WriteLine("Ignoring DirectoryNotFoundException exception while searching for WebView2 runtimes: " + e.Message);
                }

                if (potentialParents != null)
                {
                    foreach (string potentialParent in potentialParents)
                    {
                        string[] foundExes = null;
                        try
                        {
                            foundExes = Directory.GetFiles(potentialParent, "msedgewebview2.exe", SearchOption.AllDirectories);
                        }
                        catch (UnauthorizedAccessException e)
                        {
                            Debug.WriteLine("Ignoring unauthorized access exception while searching for WebView2 runtimes: " + e.Message);
                        }

                        foreach (string path in foundExes)
                        {
                            yield return new RuntimeEntry(path);
                        }
                    }
                }
            }
        }
    }
}
