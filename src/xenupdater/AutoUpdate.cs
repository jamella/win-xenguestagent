﻿/* Copyright (c) Citrix Systems Inc.
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, 
 * with or without modification, are permitted provided 
 * that the following conditions are met:
 *
 * *   Redistributions of source code must retain the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer.
 * *   Redistributions in binary form must reproduce the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer in the documentation and/or other 
 *     materials provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND 
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF 
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR 
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR 
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE 
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF 
 * SUCH DAMAGE.
 */

using System;
using System.Diagnostics;
using System.Threading;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Collections.Generic;
using TaskScheduler;
using BrandSupport;

namespace XenUpdater
{
    class AutoUpdate
    {
        XenStoreSession session;
        XenStoreItem licensed;
        XenStoreItem enabled;
        XenStoreItem update_url;
        XenStoreItem xdvdapresent;
        XenStoreItem allowdriverupdate;
        Version version;

        private object GetReg(string key, string name, object def)
        {
            try
            {
                object obj = Registry.GetValue(key, name, def);
                if (obj == null)
                    return def;
                return obj;
            }
            catch
            {
                return def;
            }
        }

        public AutoUpdate()
        {
            session = new XenStoreSession("CheckNow");
            licensed = new XenStoreItem(session, "/guest_agent_features/Guest_agent_auto_update/licensed");
            enabled = new XenStoreItem(session, "/guest_agent_features/Guest_agent_auto_update/parameters/enabled");
            update_url = new XenStoreItem(session, "/guest_agent_features/Guest_agent_auto_update/parameters/update_url");
            allowdriverupdate = new XenStoreItem(session, "/guest_agent_features/Guest_agent_auto_update/parameters/allow-driver-install");
            xdvdapresent = new XenStoreItem(session, "data/xd/present");

            int major = (int)GetReg("HKEY_LOCAL_MACHINE\\SOFTWARE\\Citrix\\XenTools", "MajorVersion", 0); 
            int minor = (int)GetReg("HKEY_LOCAL_MACHINE\\SOFTWARE\\Citrix\\XenTools", "MinorVersion", 0);
            int micro = (int)GetReg("HKEY_LOCAL_MACHINE\\SOFTWARE\\Citrix\\XenTools", "MicroVersion", 0);
            int build = (int)GetReg("HKEY_LOCAL_MACHINE\\SOFTWARE\\Citrix\\XenTools", "BuildVersion", 0);
            version = new Version(major, minor, micro, build);
        }

        private bool CheckIsAllowed()
        {
            // if licensed key doesnt exist, "missing" is returned which is not "1"
            if (licensed.ValueOrDefault("missing") != "1")
                return false;
            Version minver = new Version(6, 6, 0, 0);
            if (minver.CompareTo(version) > 0) // disallow on Cream, allow on Dundee and later
                return false;

            // disallow if enabled is present and not "1"
            string value = enabled.ValueOrDefault("missing");
            session.Log("parameters/enabled=" + value);
            if (value == "missing")
            {
                // check if XD is present and non-persistant
                if (xdvdapresent.ValueOrDefault("missing") == "1")
                    session.Log("XenDesktop is present");
                if (WmiBase.IsXDNonPersist)
                {
                    session.Log("XenDesktop is NonPersistant");
                    return false;
                }
            }
            else if (value == "0")
            {
                // check if host disallows updates
                session.Log("Pool/Host disallowed updates:" + value);
                return false;
            }

            if ((int)GetReg("HKEY_LOCAL_MACHINE\\SOFTWARE\\Citrix\\XenTools", "DisableAutoUpdate", 0) != 0)
            {
                session.Log("Guest disallowed updates");
                return false;
            }

            return true;
        }

        public void CheckNow()
        {
            if (!CheckIsAllowed())
                return; // updates disallowed, reasons already logged if important

            bool poolAllowsDriverInstall = true;
            string allowDriverUpdateValue;

            try
            {
                allowDriverUpdateValue = allowdriverupdate.Value;
                if (allowDriverUpdateValue.Equals("0") || allowDriverUpdateValue.Equals("false")) 
                {
                    poolAllowsDriverInstall = false;
                }
            }
            catch
            {
                poolAllowsDriverInstall = true;
            }

            string driverInstall = "NO";

            if (poolAllowsDriverInstall)
            {
                driverInstall = (string)GetReg("HKEY_LOCAL_MACHINE\\Software\\Citrix\\XenTools\\AutoUpdate", "InstallDrivers", Branding.GetString("allowDriverUpdate"));
                if (!(driverInstall.Equals("YES") || driverInstall.Equals("NO")))
                {
                    session.Log("Unexpected value of AutoUpdate\\InstallDrivers, assuming you meant " + Branding.GetString("allowDriverUpdate"));
                    driverInstall = Branding.GetString("allowDriverUpdate");
                }
            }
            else
            {
                session.Log("Driver update not allowed by pool");
            }

            Update update = CheckForUpdates();
            if (update == null)
                return; // no updates available, no logging

            session.Log("Update found " + update.ToString());

            string temp = DownloadUpdate(update);
            string target = GetTarget();

            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = "msiexec.exe";
            start.CreateNoWindow = true;
            start.UseShellExecute = false;
            start.RedirectStandardError = true;
            start.RedirectStandardOutput = true;
            start.Arguments = " /i \"" + temp + "\" TARGETDIR=\"" + target + "\" /log \"" + Path.Combine(target, "agent3log.log") + "\" /qn ALLOWDRIVERINSTALL="+driverInstall;

            session.Log("Killing all XenDPriv.exe processes");
            foreach (var process in Process.GetProcessesByName("XenDPriv.exe"))
                process.Kill();

            session.Log("Executing MSI with: " + start.Arguments);
            Process proc = Process.Start(start);
        }

        private Update CheckForUpdates()
        {
            string url = Branding.GetString("BRANDING_updaterURL");
            if (String.IsNullOrEmpty(url))
                url = "https://pvupdates.vmd.citrix.com/updates.v2.tsv";
            if (update_url.Exists)
                url = update_url.Value;
            url = (string)GetReg("HKEY_LOCAL_MACHINE\\SOFTWARE\\Citrix\\XenTools", "update_url", url);

            if (String.IsNullOrEmpty(url))
            {
                session.Log("Update URL is Null or Empty");
                throw new ArgumentNullException("URL is empty");
            }
            session.Log("Checking URL: " + url + " for updates after: " + version.ToString());

            string contents = null;
            try
            {
                WebClient client = new WebClient();
                contents = client.DownloadString(url);
            }
            catch (Exception e)
            {
                session.Log("Download failed " + e.Message);
                throw;
            }
            if (String.IsNullOrEmpty(contents))
                return null;

            string arch = (Win32Impl.Is64BitOS() && (!Win32Impl.IsWOW64())) ? "x64" : "x86";
            List<Update> updates = new List<Update>();
            foreach (string line in contents.Split(new char[] { '\n' }))
            {
                if (String.IsNullOrEmpty(line))
                    continue;
                try
                {
                    Update update = new Update(line);
                    session.Log("Update Entry " + update.ToString());
                    if (update.Arch != arch)
                        continue;
                    if (update.Version.CompareTo(version) <= 0)
                        continue;

                    updates.Add(update);
                }
                catch (Exception e)
                {
                    session.Log("Exception: " + e.Message);
                }
            }

            updates.Reverse();
            if (updates.Count > 0)
                return updates[0];

            session.Log("No updates found");
            return null;
        }

        private string DownloadUpdate(Update update)
        {
            string temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       update.FileName);
            try
            {
                if (File.Exists(temp))
                {
                    session.Log("Deleting existing MSI at " + temp);
                    File.Delete(temp);
                }

                session.Log("Downloading: " + update.Url);

                Downloader down = new Downloader();
                if (!down.Download(update.Url, temp, update.Size))
                    throw new ArgumentException("Update was incorrect size " + update.Url + " > " + update.Size.ToString() + " bytes");

                session.Log("MSI downloaded to " + temp + ", checking signature");

                if (!VerifyCertificate(temp))
                    throw new UnauthorizedAccessException("Certificate subject is invalid");

                session.Log("MSI downloaded to: " + temp);
                return temp;
            }
            catch (Exception e)
            {
				session.Log("Exception: " + e.Message);
                if (File.Exists(temp))
                {
                    session.Log("Deleting file at " + temp);
                    File.Delete(temp);
                }
                throw;
            }
        }

        private bool VerifyCertificate(string filename)
        {
            try
            {
                X509Certificate signer = X509Certificate.CreateFromSignedFile(filename);
                if (!signer.Subject.Contains("O=\"Citrix Systems, Inc.\""))
                    return false;
                X509Certificate2 cert = new X509Certificate2(signer);
                X509Chain chain = new X509Chain();
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                chain.ChainPolicy.UrlRetrievalTimeout = new TimeSpan(0, 1, 0);
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                return chain.Build(cert);
            }
            catch (CryptographicException e)
            {
                session.Log("Exception in VerifyCertificate, " + e.Message);
                throw new CryptographicException(string.Format("Certificate check failure on installer, {0}", filename), e);
            }
        }

        private string GetTarget()
        {
            // "<Program Files>\Citrix\XenTools"
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Citrix\\XenTools");
            string regPath = (Win32Impl.Is64BitOS()) ? 
                            "HKEY_LOCAL_MACHINE\\SOFTWARE\\Wow6432Node\\Citrix\\XenTools" :
                            "HKEY_LOCAL_MACHINE\\SOFTWARE\\Citrix\\XenTools";
            string targetPath = (string)Registry.GetValue(regPath, "Install_Dir", path);
            return String.IsNullOrEmpty(targetPath) ? path : targetPath;
        }

        class Update
        {
            internal string Arch { get; private set; }
            internal string FileName { get; private set; }
            internal string Url { get; private set; }
            internal Version Version { get; private set; }
            internal int Size { get; private set; }
            internal int Checksum { get; private set; }

            internal Update(string line)
            {
                // Line format = URL\tVERSION\tSIZE\tARCH\tCHECKSUM
                string[] s = line.Split(new char[] { '\t' });
                if (s.Length < 3)
                    throw new FormatException("Invalid update format :" + line);

                Url = s[0];
                Version = new Version(s[1]);
                Size = int.Parse(s[2]);

                if (s.Length < 4)
                    Arch = ParseArch(s[0]);
                else
                    Arch = ParseArch(s[3]);

                if (s.Length < 5)
                    Checksum = -1;
                else
                    Checksum = int.Parse(s[4]);

                FileName = Url.Substring(s[0].LastIndexOf('/') + 1);
            }
            private string ParseArch(string s)
            {
                if (s.Contains("x86") || s.Contains("X86"))
                    return "x86";
                if (s.Contains("x64") || s.Contains("X64"))
                    return "x64";
                throw new FormatException("Invalid update format");
            }

            public override string ToString()
            {
                return Arch + " > " + Url + " " + this.Version.ToString() + " = " + Size.ToString() + " (" + Checksum.ToString() + ")";
            }
        }

        class Downloader
        {
            public Downloader()
            {
                finished = new AutoResetEvent(false);
            }

            public bool Download(string url, string file, int maxsize)
            {
                maxSize = maxsize;
                complete = false;
				error = false;

                client = new WebClient();
                client.DownloadFileCompleted += new System.ComponentModel.AsyncCompletedEventHandler(DownloadCompleted);
                client.DownloadProgressChanged += new DownloadProgressChangedEventHandler(DownloadProgressChanged);
                client.DownloadFileAsync(new Uri(url), file);

                finished.WaitOne(900000); // 15 min
                return complete && !error;
            }

            private void DownloadCompleted(object sender, System.ComponentModel.AsyncCompletedEventArgs evt)
            {
                if (evt.Cancelled || evt.Error != null)
                    error = true;
				complete = true;
                finished.Set();
            }

            private void DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs evt)
            {
                if (evt.BytesReceived > maxSize)
                {
                    error = true;
                    client.CancelAsync();
                    finished.Set();
                }
            }

            private WebClient client;
            private int maxSize;
            private bool complete;
			private bool error;
            private AutoResetEvent finished;
        }

        class Win32Impl
        {
            static public bool Is64BitOS()
            {
                if (IntPtr.Size == 8)
                    return true;
                return IsWOW64();
            }

            static public bool IsWOW64()
            {
                bool flags;
                IntPtr modhandle = GetModuleHandle("kernel32.dll");
                if (modhandle == IntPtr.Zero)
                    return false;
                if (GetProcAddress(modhandle, "IsWow64Process") == IntPtr.Zero)
                    return false;

                if (IsWow64Process(GetCurrentProcess(), out flags))
                    return flags;
                return false;
            }

            [DllImport("kernel32.dll")]
            static extern IntPtr GetCurrentProcess();

            [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
            static extern IntPtr GetModuleHandle(string moduleName);

            [DllImport("kernel32", CharSet = CharSet.Auto, SetLastError = true)]
            static extern IntPtr GetProcAddress(IntPtr hModule,
                [MarshalAs(UnmanagedType.LPStr)]string procName);

            [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);
        }
    }
}
