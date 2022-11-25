﻿/*
Copyright 2022 CnCNet

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Reflection;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using Rampastring.Tools;
using ClientUpdater.Compression;

namespace ClientUpdater
{
    public static class Updater
    {
        #region constants

        public const string SECOND_STAGE_UPDATER = "SecondStageUpdater.exe";
        public static string VERSION_FILE = "version";
        public const string ARCHIVE_FILE_EXTENSION = ".lzma";

        #endregion

        #region public_properties

        /// <summary>
        /// Currently set game path for the updater.
        /// </summary>
        public static string GamePath { get; private set; } = string.Empty;

        /// <summary>
        /// Currently set resource path for the updater.
        /// </summary>
        public static string ResourcePath { get; private set; } = string.Empty;

        /// <summary>
        /// Currently set local game ID for the updater.
        /// </summary>
        public static string LocalGame { get; private set; } = "None";

        /// <summary>
        /// Currently set calling executable file name for the updater.
        /// </summary>
        public static string CallingExecutableFileName { get; private set; } = string.Empty;

        /// <summary>
        /// Gets read-only collection of all custom components.
        /// </summary>
        public static ReadOnlyCollection<CustomComponent> CustomComponents => customComponents?.AsReadOnly();

        /// <summary>
        /// Gets read-only collection of all update mirrors.
        /// </summary>
        public static ReadOnlyCollection<UpdateMirror> UpdateMirrors => updateMirrors?.AsReadOnly();

        /// <summary>
        /// Update server URL for current update mirror if available.
        /// </summary>
        public static string CurrentUpdateServerURL => updateMirrors != null && updateMirrors.Count > 0 ?
            updateMirrors[currentUpdateMirrorIndex].URL : null;

        private static VersionState _versionState = VersionState.UNKNOWN;

        /// <summary>
        /// Current version state of the updater.
        /// </summary>
        public static VersionState VersionState
        {
            get { return _versionState; }

            private set
            {
                _versionState = value;
                DoOnVersionStateChanged();
            }
        }

        /// <summary>
        /// Does the currently available update (if applicable) require manual download?
        /// </summary>
        public static bool ManualUpdateRequired { get; private set; } = false;

        /// <summary>
        /// Manual download URL for currently available update, if available.
        /// </summary>
        public static string ManualDownloadURL { get; private set; } = string.Empty;

        /// <summary>
        /// Local version file updater version.
        /// </summary>
        public static string UpdaterVersion { get; private set; } = "N/A";

        /// <summary>
        /// Local version file game version.
        /// </summary>
        public static string GameVersion { get; private set; } = "N/A";

        /// <summary>
        /// Server version file game version.
        /// </summary>
        public static string ServerGameVersion { get; private set; } = "N/A";

        /// <summary>
        /// Size of current update in kilobytes.
        /// </summary>
        public static int UpdateSizeInKb { get; private set; }

        #endregion

        #region private_fields

        // Misc.
        private static int currentUpdateMirrorIndex;
        private static IniFile settingsINI;
        private static List<CustomComponent> customComponents;
        private static List<UpdateMirror> updateMirrors;
        private static string[] ignoreMasks = new string[] { ".rtf", ".txt", "Theme.ini", "gui_settings.xml" };

        // File infos.
        private readonly static List<UpdaterFileInfo> FileInfosToDownload = new List<UpdaterFileInfo>();
        private readonly static List<UpdaterFileInfo> ServerFileInfos = new List<UpdaterFileInfo>();
        public readonly static List<UpdaterFileInfo> LocalFileInfos = new List<UpdaterFileInfo>();

        // Current update / download related.
        private static bool terminateUpdate = false;
        private static string currentFilename;
        private static int currentFileSize;
        private static int totalDownloadedKbs;
        private static Exception currentDownloadException;

        #endregion

        #region public_methods

        /// <summary>
        /// Initializes the updater.
        /// </summary>
        /// <param name="gamePath">Path of the root client / game folder.</param>
        /// <param name="resourcePath">Path of the resource folder of client / game.</param>
        /// <param name="settingsIniName">Client settings INI filename.</param>
        /// <param name="localGame">Local game ID of the current game.</param>
        /// <param name="callingExecutableFileName">File name of the calling executable.</param>
        public static void Initialize(string gamePath, string resourcePath, string settingsIniName, string localGame, string callingExecutableFileName)
        {
            Logger.Log("Updater: Initializing updater.");

            GamePath = gamePath;
            ResourcePath = resourcePath;
            settingsINI = new IniFile(SafePath.CombineFilePath(GamePath, settingsIniName));
            LocalGame = localGame;
            CallingExecutableFileName = callingExecutableFileName;

            ReadUpdaterConfig();

            Logger.Log("Updater: Update mirror count: " + updateMirrors.Count);
            Logger.Log("Updater: Running from: " + CallingExecutableFileName);
            List<UpdateMirror> list = new List<UpdateMirror>();
            List<string> sectionKeys = settingsINI.GetSectionKeys("DownloadMirrors");

            if (sectionKeys != null)
            {
                foreach (string str in sectionKeys)
                {
                    string value = settingsINI.GetStringValue("DownloadMirrors", str, string.Empty);
                    UpdateMirror item = updateMirrors.Find(um => um.Name == value);
                    if ((item != null) && !list.Contains(item))
                    {
                        list.Add(item);
                    }
                }
            }
            foreach (UpdateMirror mirror2 in updateMirrors)
            {
                if (!list.Contains(mirror2))
                {
                    list.Add(mirror2);
                }
            }
            updateMirrors = list;
        }

        /// <summary>
        /// Checks if there are available updates.
        /// </summary>
        public static void CheckForUpdates()
        {
            Logger.Log("Updater: Checking for updates.");
            if (VersionState != VersionState.UPDATECHECKINPROGRESS && VersionState != VersionState.UPDATEINPROGRESS)
            {
                new Thread(new ThreadStart(DoVersionCheck)).Start();
            }
        }

        /// <summary>
        /// Checks version information of local files.
        /// </summary>
        public static void CheckLocalFileVersions()
        {
            Logger.Log("Updater: Checking local file versions.");

            LocalFileInfos.Clear();

            IniFile file = new IniFile(SafePath.CombineFilePath(GamePath, VERSION_FILE));
            GameVersion = file.GetStringValue("DTA", "Version", "N/A");
            UpdaterVersion = file.GetStringValue("DTA", "UpdaterVersion", "N/A");
            List<string> sectionKeys = file.GetSectionKeys("FileVersions");

            if (sectionKeys != null)
            {
                foreach (string str in sectionKeys)
                {
                    char[] separator = new char[] { ',' };
                    string[] strArray = file.GetStringValue("FileVersions", str, string.Empty).Split(separator);
                    string[] strArrayArch = file.GetStringValue("ArchivedFiles", str, string.Empty).Split(separator);
                    bool archiveAvailable = strArrayArch != null && strArrayArch.Length >= 2;

                    if (strArray.Length >= 2)
                    {
                        UpdaterFileInfo item = new UpdaterFileInfo
                        {
                            Filename = SafePath.CombineFilePath(str),
                            Identifier = strArray[0],
                            Size = Conversions.IntFromString(strArray[1], 0),
                            ArchiveIdentifier = archiveAvailable ? strArrayArch[0] : string.Empty,
                            ArchiveSize = archiveAvailable ? Conversions.IntFromString(strArrayArch[1], 0) : 0
                        };

                        LocalFileInfos.Add(item);
                    }
                    else
                    {
                        Logger.Log("Updater: Warning: Malformed file info in local version information: " + str);
                    }
                }
            }

            OnLocalFileVersionsChecked?.Invoke();
        }

        /// <summary>
        /// Starts update process.
        /// </summary>
        public static void StartUpdate() => new Thread(new ThreadStart(PerformUpdate)).Start();

        /// <summary>
        /// Stops current update process.
        /// </summary>
        public static void StopUpdate() => terminateUpdate = true;

        /// <summary>
        /// Clears current version file information.
        /// </summary>
        public static void ClearVersionInfo()
        {
            LocalFileInfos.Clear();
            ServerFileInfos.Clear();
            FileInfosToDownload.Clear();
            GameVersion = "N/A";
            VersionState = VersionState.UNKNOWN;
        }

        /// <summary>
        /// Checks if file 
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        public static bool IsFileNonexistantOrOriginal(string filePath)
        {
            UpdaterFileInfo info = LocalFileInfos.Find(f => f.Filename.Equals(filePath, StringComparison.OrdinalIgnoreCase));

            if (info == null)
                return true;

            string uniqueIdForFile = GetUniqueIdForFile(info.Filename);
            return (info.Identifier == uniqueIdForFile);
        }

        /// <summary>
        /// Moves update mirror down in list of update mirrors.
        /// </summary>
        /// <param name="mirrorIndex">Index of mirror to move in the list.</param>
        public static void MoveMirrorDown(int mirrorIndex)
        {
            if (mirrorIndex > updateMirrors.Count - 2 || mirrorIndex < 0)
                return;

            UpdateMirror tmp = updateMirrors[mirrorIndex + 1];
            updateMirrors[mirrorIndex + 1] = updateMirrors[mirrorIndex];
            updateMirrors[mirrorIndex] = tmp;
        }

        /// <summary>
        /// Moves update mirror up in list of update mirrors.
        /// </summary>
        /// <param name="mirrorIndex">Index of mirror to move in the list.</param>
        public static void MoveMirrorUp(int mirrorIndex)
        {
            if (updateMirrors.Count <= mirrorIndex || mirrorIndex < 1)
                return;

            UpdateMirror tmp = updateMirrors[mirrorIndex - 1];
            updateMirrors[mirrorIndex - 1] = updateMirrors[mirrorIndex];
            updateMirrors[mirrorIndex] = tmp;
        }

        /// <summary>
        /// Returns whether or not there is a currently active custom component in progress.
        /// </summary>
        /// <returns>True if custom component download is in progress, otherwise false.</returns>
        public static bool IsComponentDownloadInProgress()
        {
            if (customComponents == null)
                return false;

            return customComponents.Any(c => c.IsBeingDownloaded);
        }

        /// <summary>
        /// Gets custom component index based on name.
        /// </summary>
        /// <param name="componentName">Name of custom component.</param>
        /// <returns>Component index if found, otherwise -1.</returns>
        public static int GetComponentIndex(string componentName)
        {
            if (customComponents == null)
                return -1;

            return customComponents.FindIndex(c => c.ININame == componentName);
        }

        #endregion

        #region internal_methods

        /// <summary>
        /// Get archive info for a file from version file.
        /// </summary>
        /// <param name="versionFile">Version file.</param>
        /// <param name="filename">Filename.</param>
        /// <param name="archiveID">Set to archive ID.</param>
        /// <param name="archiveSize">Set to archive file size.</param>
        internal static void GetArchiveInfo(IniFile versionFile, string filename, out string archiveID, out int archiveSize)
        {
            string[] values = versionFile.GetStringValue("ArchivedFiles", filename, "").Split(',');
            bool archiveAvailable = values != null && values.Length >= 2;
            archiveID = archiveAvailable ? values[0] : "";
            archiveSize = archiveAvailable ? Conversions.IntFromString(values[1], 0) : 0;
        }

        /// <summary>
        /// Creates file info instance from given information.
        /// </summary>
        /// <param name="filename">Filename.</param>
        /// <param name="identifier">File identifier.</param>
        /// <param name="size">File size.</param>
        /// <param name="archiveIdentifier">Archive file identifier.</param>
        /// <param name="archiveSize">Archive file size.</param>
        /// <returns></returns>
        internal static UpdaterFileInfo CreateFileInfo(string filename, string identifier, int size, string archiveIdentifier = null, int archiveSize = 0)
        {
            return new UpdaterFileInfo
            {
                Filename = SafePath.CombineFilePath(filename),
                Identifier = identifier,
                Size = size,
                ArchiveIdentifier = archiveIdentifier,
                ArchiveSize = archiveSize
            };
        }

        /// <summary>
        /// Gets user agent information as a string.
        /// </summary>
        /// <returns>User agent string.</returns>
        internal static string GetUserAgentString()
        {
            string updaterString = "";

            if (UpdaterVersion != "N/A")
                updaterString = " Updater/" + UpdaterVersion;

            return LocalGame + updaterString + " Game/" + GameVersion + " Client/" + Assembly.GetEntryAssembly().GetName().Version;
        }

        /// <summary>
        /// Deletes file and waits until it has been deleted.
        /// </summary>
        /// <param name="filepath">File to delete.</param>
        /// <param name="timeout">Maximum time to wait in milliseconds.</param>
        internal static void DeleteFileAndWait(string filepath, int timeout = 10000)
        {
            FileInfo fileInfo = SafePath.GetFile(filepath);
            using (var fw = new FileSystemWatcher(fileInfo.DirectoryName, fileInfo.Name))
            using (var mre = new ManualResetEventSlim())
            {
                fw.EnableRaisingEvents = true;
                fw.Deleted += (object sender, FileSystemEventArgs e) =>
                {
                    mre.Set();
                };
                fileInfo.Delete();
                mre.Wait(timeout);
            }
        }

        /// <summary>
        /// Creates all directories required for file path.
        /// </summary>
        /// <param name="filePath">File path.</param>
        internal static void CreatePath(string filePath)
        {
            FileInfo fileInfo = SafePath.GetFile(filePath);

            if (!fileInfo.Directory.Exists)
                fileInfo.Directory.Create();
        }

        internal static string GetUniqueIdForFile(string filePath)
        {
            MD5 md = MD5.Create();
            md.Initialize();
            using FileStream fs = SafePath.GetFile(GamePath, filePath).OpenRead();
            md.ComputeHash(fs);
            StringBuilder builder = new StringBuilder();
            foreach (byte num2 in md.Hash)
            {
                builder.Append(num2.ToString());
            }
            md.Clear();
            return builder.ToString();
        }

        #endregion

        #region private_methods

        /// <summary>
        /// Parse updater configuration file.
        /// </summary>
        private static void ReadUpdaterConfig()
        {
            List<UpdateMirror> mirrors = new List<UpdateMirror>();
            List<CustomComponent> customComponents = new List<CustomComponent>();

            FileInfo configFile = SafePath.GetFile(ResourcePath, "UpdaterConfig.ini");

            if (!configFile.Exists)
            {
                Logger.Log("Updater config file not found - attempting to read legacy updateconfig.ini.");
                ReadLegacyUpdaterConfig(mirrors);
            }
            else
            {
                IniFile updaterConfig = new IniFile(configFile.FullName);
                ignoreMasks = updaterConfig.GetStringValue("Settings", "IgnoreMasks", string.Join(",", ignoreMasks)).Split(',');

                List<string> keys = updaterConfig.GetSectionKeys("DownloadMirrors");

                if (keys != null)
                {
                    foreach (string key in keys)
                    {
                        if (string.IsNullOrEmpty(key))
                            continue;

                        string stringValue = updaterConfig.GetStringValue("DownloadMirrors", key, string.Empty);

                        if (string.IsNullOrEmpty(stringValue))
                            stringValue = key;

                        string[] values = stringValue.Split(',');

                        if (values.Length < 2)
                            continue;
                        if (mirrors.FindIndex(i => i.URL == values[0].Trim()) < 0)
                            mirrors.Add(new UpdateMirror(values[0].Trim(), values[1].Trim(), values.Length > 2 ? values[2].Trim() : ""));
                    }
                }

                keys = updaterConfig.GetSectionKeys("CustomComponents");

                if (keys != null)
                {
                    foreach (string key in keys)
                    {
                        if (string.IsNullOrEmpty(key))
                            continue;

                        string stringValue = updaterConfig.GetStringValue("CustomComponents", key, string.Empty);

                        if (string.IsNullOrEmpty(stringValue))
                            stringValue = key;

                        string[] values = stringValue.Split(',');

                        if (values.Length < 4)
                            continue;

                        string ID = values[1].Trim();

                        if (customComponents.FindIndex(i => i.ININame == ID) < 0)
                        {
                            string Name = values[0].Trim();
                            string DownloadPath = values[2].Trim();
                            string LocalPath = values[3].Trim();
                            bool noArchiveExtensionForDownloadPath = false;

                            if (values.Length > 4)
                                noArchiveExtensionForDownloadPath = Conversions.BooleanFromString(values[4], false);

                            bool DownloadPathIsAbsolute = Uri.IsWellFormedUriString(DownloadPath, UriKind.Absolute);
                            customComponents.Add(new CustomComponent(Name, ID, DownloadPath, LocalPath, DownloadPathIsAbsolute, noArchiveExtensionForDownloadPath));
                        }
                    }
                }
            }

            updateMirrors = mirrors;
            Updater.customComponents = customComponents;

            if (updateMirrors.Count < 1)
                Logger.Log("Warning: No download mirrors found in updater config file or the built-in game info.");
        }

        /// <summary>
        /// Parse legacy format updater configuration file.
        /// </summary>
        /// <param name="updateMirrors">List of update mirrors to add update mirrors to.</param>
        private static void ReadLegacyUpdaterConfig(List<UpdateMirror> updateMirrors)
        {
            FileInfo updateConfigFile = SafePath.GetFile(GamePath, "updateconfig.ini");

            if (!updateConfigFile.Exists)
                return;

            string[] lines;

            try
            {
                lines = File.ReadAllLines(updateConfigFile.FullName);
            }
            catch (Exception e)
            {
                Logger.Log("Error: Could not read legacy format updateconfig.ini. Message:" + e.Message);
                return;
            }

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.Trim().StartsWith(";"))
                    continue;

                string[] array = line.Split(new char[] { ',' });

                if (array.Length < 3)
                    continue;

                string url = array[0].Trim();
                string name = array[1].Trim();
                string location = array[2].Trim();
                updateMirrors.Add(new UpdateMirror(url, name, location));
            }
        }

        /// <summary>
        /// Performs a version file check on update server.
        /// </summary>
        private static void DoVersionCheck()
        {
            Logger.Log("Updater: Doing version file check.");

            ServerFileInfos.Clear();
            FileInfosToDownload.Clear();
            UpdateSizeInKb = 0;

            try
            {
                VersionState = VersionState.UPDATECHECKINPROGRESS;

                if (updateMirrors.Count == 0)
                {
                    Logger.Log("Updater: There are no update mirrors!");
                }
                else
                {
                    Logger.Log("Updater: Checking version on the server.");

                    using WebClient client = new WebClient
                    {
                        CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore)
                    };

                    client.Headers.Add(HttpRequestHeader.UserAgent, GetUserAgentString());

                    FileInfo downloadFile = SafePath.GetFile(GamePath, FormattableString.Invariant($"{VERSION_FILE}_u"));

                    while (currentUpdateMirrorIndex < updateMirrors.Count)
                    {
                        try
                        {
                            Logger.Log("Updater: Trying to connect to update mirror " + updateMirrors[currentUpdateMirrorIndex].URL);
                            client.DownloadFile(updateMirrors[currentUpdateMirrorIndex].URL + VERSION_FILE, downloadFile.FullName);
                            break;
                        }
                        catch (Exception e)
                        {
                            Logger.Log("Updater: Error connecting to update mirror. Error message: " + e.Message);
                            Logger.Log("Updater: Seeking other mirrors...");
                            currentUpdateMirrorIndex++;

                            if (currentUpdateMirrorIndex >= updateMirrors.Count)
                            {
                                currentUpdateMirrorIndex = 0;
                                throw new Exception("Unable to connect to update servers.");
                            }

                            continue;
                        }
                    }

                    Logger.Log("Updater: Downloaded version information.");
                    IniFile version = new IniFile(downloadFile.FullName);
                    string versionString = version.GetStringValue("DTA", "Version", "");
                    string updaterVersionString = version.GetStringValue("DTA", "UpdaterVersion", "N/A");
                    string manualDownloadURLString = version.GetStringValue("DTA", "ManualDownloadURL", "");

                    if (version.SectionExists("FileVersions"))
                    {
                        foreach (string key in version.GetSectionKeys("FileVersions"))
                        {
                            string[] tmp = version.GetStringValue("FileVersions", key, "").Split(',');

                            if (tmp.Length < 2)
                            {
                                Logger.Log("Updater: Warning: Malformed file info in downloaded version information: " + key);
                                continue;
                            }

                            GetArchiveInfo(version, key, out string archiveID, out int archiveSize);
                            UpdaterFileInfo item = CreateFileInfo(key, tmp[0], Conversions.IntFromString(tmp[1], 0), archiveID, archiveSize);
                            ServerFileInfos.Add(item);
                        }
                    }

                    if (version.SectionExists("AddOns"))
                    {
                        foreach (string key in version.GetSectionKeys("AddOns"))
                        {
                            string[] tmp = version.GetStringValue("AddOns", key, "").Split(',');

                            if (tmp.Length < 2)
                            {
                                Logger.Log("Updater: Warning: Malformed addon info in downloaded version information: " + key);
                                continue;
                            }

                            UpdaterFileInfo item = CreateFileInfo(key, tmp[0], Conversions.IntFromString(tmp[1], 0), "", 0);
                            int index = GetComponentIndex(key);

                            if (index == -1)
                            {
                                Logger.Log("Updater: Warning: Invalid custom component ID " + key);
                            }
                            else
                            {
                                CustomComponent component = customComponents[index];
                                component.Initialized = false;
                                Logger.Log("Updater: Setting custom component info for " + key);
                                GetArchiveInfo(version, component.LocalPath, out string archiveID, out int archiveSize);
                                item.ArchiveIdentifier = archiveID;
                                item.ArchiveSize = archiveSize;
                                component.RemoteSize = item.Size * 1024;
                                component.RemoteArchiveSize = item.Archived ? item.ArchiveSize * 1024 : 0;
                                component.RemoteIdentifier = item.Identifier;
                                component.Archived = item.Archived;

                                if (SafePath.GetFile(GamePath, component.LocalPath).Exists)
                                    component.LocalIdentifier = GetUniqueIdForFile(component.LocalPath);

                                component.Initialized = true;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(versionString))
                    {
                        throw new Exception("Update server integrity error while checking for updates.");
                    }

                    Logger.Log("Updater: Server game version is " + versionString + ", local version is " + GameVersion);
                    ServerGameVersion = versionString;

                    if (versionString == GameVersion)
                    {
                        VersionState = VersionState.UPTODATE;
                        downloadFile.Delete();
                        DoFileIdentifiersUpdatedEvent();

                        if (AreCustomComponentsOutdated())
                            DoCustomComponentsOutdatedEvent();
                    }
                    else
                    {
                        if (updaterVersionString != "N/A" && UpdaterVersion != updaterVersionString)
                        {
                            Logger.Log("Updater: Server update system version is set to " + updaterVersionString + " and is different to local update system version " + UpdaterVersion + ". Manual update required.");
                            VersionState = VersionState.OUTDATED;
                            ManualUpdateRequired = true;
                            ManualDownloadURL = manualDownloadURLString;
                            downloadFile.Delete();
                            DoFileIdentifiersUpdatedEvent();
                        }
                        else
                        {
                            VersionCheckHandle();
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                VersionState = VersionState.UNKNOWN;
                Logger.Log("Updater: An error occured while performing version check: " + exception.Message);
                DoFileIdentifiersUpdatedEvent();
            }
        }

        /// <summary>
        /// Checks if custom components are outdated.
        /// </summary>
        /// <returns>True if custom components are outdated, otherwise false.</returns>
        private static bool AreCustomComponentsOutdated()
        {
            Logger.Log("Updater: Checking if custom components are outdated.");
            foreach (CustomComponent component in customComponents)
            {
                if (SafePath.GetFile(GamePath, component.LocalPath).Exists && (component.RemoteIdentifier != component.LocalIdentifier))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Executes after-update script file.
        /// </summary>
        private static void ExecuteAfterUpdateScript()
        {
            Logger.Log("Updater: Downloading updateexec.");
            try
            {
                using WebClient client = new WebClient
                {
                    CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore)
                };
                client.Headers.Add(HttpRequestHeader.UserAgent, GetUserAgentString());
                client.DownloadProgressChanged += new DownloadProgressChangedEventHandler(WebClient_DownloadProgressChanged);
                client.DownloadFile(updateMirrors[currentUpdateMirrorIndex].URL + "updateexec", SafePath.CombineFilePath(GamePath, "updateexec"));
                client.CancelAsync();
            }
            catch (Exception exception)
            {
                Logger.Log("Updater: Warning: Downloading updateexec failed: " + exception.Message);
                return;
            }

            ExecuteScript("updateexec");
        }

        /// <summary>
        /// Executes pre-update script file.
        /// </summary>
        /// <returns>True if succesful, otherwise false.</returns>
        private static bool ExecutePreUpdateScript()
        {
            Logger.Log("Updater: Downloading preupdateexec.");
            try
            {
                using WebClient client = new WebClient
                {
                    CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore)
                };
                client.Headers.Add(HttpRequestHeader.UserAgent, GetUserAgentString());
                client.DownloadProgressChanged += new DownloadProgressChangedEventHandler(WebClient_DownloadProgressChanged);
                client.DownloadFile(updateMirrors[currentUpdateMirrorIndex].URL + "preupdateexec", SafePath.CombineFilePath(GamePath, "preupdateexec"));
            }
            catch (Exception exception)
            {
                Logger.Log("Updater: Warning: Downloading preupdateexec failed: " + exception.Message);
                return false;
            }

            ExecuteScript("preupdateexec");
            return true;
        }

        /// <summary>
        /// Executes a script file.
        /// </summary>
        /// <param name="fileName">Filename of the script file.</param>
        private static void ExecuteScript(string fileName)
        {
            Logger.Log("Updater: Executing " + fileName + ".");
            FileInfo scriptFileInfo = SafePath.GetFile(GamePath, fileName);
            IniFile script = new IniFile(scriptFileInfo.FullName);

            // Delete files.
            foreach (string key in GetKeys(script, "Delete"))
            {
                Logger.Log("Updater: " + fileName + ": Deleting file " + key);

                try
                {
                    SafePath.DeleteFileIfExists(GamePath, key);
                }
                catch (Exception ex)
                {
                    Logger.Log("Updater: " + fileName + ": Deleting file " + key + "failed: " + ex.Message);
                }
            }

            // Rename files.
            foreach (string key in GetKeys(script, "Rename"))
            {
                string newFilename = SafePath.CombineFilePath(script.GetStringValue("Rename", key, ""));
                if (string.IsNullOrWhiteSpace(newFilename))
                    continue;
                try
                {
                    Logger.Log("Updater: " + fileName + ": Renaming file '" + key + "' to '" + newFilename + "'");

                    FileInfo file = SafePath.GetFile(GamePath, key);

                    if (file.Exists)
                        file.MoveTo(SafePath.CombineFilePath(GamePath, newFilename));
                }
                catch (Exception ex)
                {
                    Logger.Log("Updater: " + fileName + ": Renaming file '" + key + "' to '" + newFilename + "' failed: " + ex.Message);
                }
            }

            // Rename folders.
            foreach (string key in GetKeys(script, "RenameFolder"))
            {
                string newDirectoryName = script.GetStringValue("RenameFolder", key, "");
                if (string.IsNullOrWhiteSpace(newDirectoryName))
                    continue;
                try
                {
                    Logger.Log("Updater: " + fileName + ": Renaming directory '" + key + "' to '" + newDirectoryName + "'");

                    DirectoryInfo directory = SafePath.GetDirectory(GamePath, key);

                    if (directory.Exists)
                        directory.MoveTo(SafePath.CombineDirectoryPath(GamePath, newDirectoryName));
                }
                catch (Exception ex)
                {
                    Logger.Log("Updater: " + fileName + ": Renaming directory '" + key + "' to '" + newDirectoryName + "' failed: " + ex.Message);
                }
            }

            // Rename & merge files / folders.
            foreach (string key in GetKeys(script, "RenameAndMerge"))
            {
                string directoryName = key;
                string directoryNameToMergeInto = script.GetStringValue("RenameAndMerge", key, "");
                if (string.IsNullOrWhiteSpace(directoryNameToMergeInto))
                    continue;
                try
                {
                    Logger.Log("Updater: " + fileName + ": Merging directory '" + directoryName + "' with '" + directoryNameToMergeInto + "'");
                    DirectoryInfo directoryToMergeInto = SafePath.GetDirectory(GamePath, directoryNameToMergeInto);
                    DirectoryInfo gameDirectory = SafePath.GetDirectory(GamePath, directoryName);

                    if (!gameDirectory.Exists)
                        continue;

                    if (!directoryToMergeInto.Exists)
                    {
                        Logger.Log("Updater: " + fileName + ": Destination directory '" + directoryNameToMergeInto + "' does not exist, renaming.");
                        gameDirectory.MoveTo(directoryToMergeInto.FullName);
                    }
                    else
                    {
                        Logger.Log("Updater: " + fileName + ": Destination directory '" + directoryNameToMergeInto + "' exists, performing selective merging.");
                        FileInfo[] files = gameDirectory.GetFiles();
                        foreach (FileInfo file in files)
                        {
                            FileInfo fileToMergeInto = SafePath.GetFile(directoryToMergeInto.FullName, file.Name);
                            if (fileToMergeInto.Exists)
                            {
                                Logger.Log("Updater: " + fileName + ": Destination file '" + directoryNameToMergeInto + "/" + file.Name +
                                    "' exists, removing original source file " + directoryName + "/" + file.Name);
                                fileToMergeInto.Delete();
                            }
                            else
                            {
                                Logger.Log("Updater: " + fileName + ": Destination file '" + directoryNameToMergeInto + "/" + file.Name +
                                    "' does not exist, moving original source file " + directoryName + "/" + file.Name);
                                file.MoveTo(fileToMergeInto.FullName);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("Updater: " + fileName + ": Merging directory '" + directoryName + "' with '" + directoryNameToMergeInto + "' failed: " + ex.Message);
                }
            }

            // Delete folders.
            foreach (string sectionName in new string[] { "DeleteFolder", "ForceDeleteFolder" })
            {
                foreach (string key in GetKeys(script, sectionName))
                {
                    try
                    {
                        Logger.Log("Updater: " + fileName + ": Deleting directory '" + key + "'");

                        SafePath.DeleteDirectoryIfExists(true, GamePath, key);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("Updater: " + fileName + ": Deleting directory '" + key + "' failed: " + ex.Message);
                    }
                }
            }

            // Delete folders, if empty.
            foreach (string key in GetKeys(script, "DeleteFolderIfEmpty"))
            {
                try
                {
                    Logger.Log("Updater: " + fileName + ": Deleting directory '" + key + "' if it's empty.");

                    DirectoryInfo directoryInfo = SafePath.GetDirectory(GamePath, key);

                    if (directoryInfo.Exists)
                    {
                        if (!directoryInfo.EnumerateFiles().Any())
                        {
                            directoryInfo.Delete();
                        }
                        else
                        {
                            Logger.Log("Updater: " + fileName + ": Directory '" + key + "' is not empty!");
                        }
                    }
                    else
                    {
                        Logger.Log("Updater: " + fileName + ": Specified directory does not exist.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("Updater: " + fileName + ": Deleting directory '" + key + "' if it's empty failed: " + ex.Message);
                }
            }

            // Create folders.
            foreach (string key in GetKeys(script, "CreateFolder"))
            {
                try
                {
                    DirectoryInfo directoryInfo = SafePath.GetDirectory(GamePath, key);
                    if (!directoryInfo.Exists)
                    {
                        Logger.Log("Updater: " + fileName + ": Creating directory '" + key + "'");
                        directoryInfo.Create();
                    }
                    else
                    {
                        Logger.Log("Updater: " + fileName + ": Directory '" + key + "' already exists.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("Updater: " + fileName + ": Creating directory '" + key + "' failed: " + ex.Message);
                }
            }

            scriptFileInfo.Delete();
        }

        /// <summary>
        /// Handle version check.
        /// </summary>
        private static void VersionCheckHandle()
        {
            Logger.Log("Updater: Gathering list of files to be downloaded. Server file info count: " + ServerFileInfos.Count);

            FileInfosToDownload.Clear();

            for (int i = 0; i < ServerFileInfos.Count; i++)
            {
                string identifier = ServerFileInfos[i].Identifier;
                bool flag = false;
                FileInfo serverFileInfo = SafePath.GetFile(GamePath, ServerFileInfos[i].Filename);

                for (int k = 0; k < LocalFileInfos.Count; k++)
                {
                    UpdaterFileInfo info = LocalFileInfos[k];

                    if (ServerFileInfos[i].Filename == info.Filename)
                    {
                        flag = true;

                        if (!serverFileInfo.Exists)
                        {
                            Logger.Log("Updater: File " + ServerFileInfos[i].Filename + " not found. Adding it to the download queue.");
                            FileInfosToDownload.Add(ServerFileInfos[i]);
                        }
                        else if (info.Identifier != identifier)
                        {
                            Logger.Log("Updater: Local file " + info.Filename + " is different, adding it to the download queue.");
                            FileInfosToDownload.Add(ServerFileInfos[i]);
                        }
                    }
                }
                if (!flag)
                {
                    Logger.Log("Updater: File " + ServerFileInfos[i].Filename + " doesn't exist on local version information - checking if it exists in the directory.");

                    if (serverFileInfo.Exists)
                    {
                        if (TryGetUniqueId(ServerFileInfos[i].Filename) != identifier)
                        {
                            Logger.Log("Updater: File " + ServerFileInfos[i].Filename + " is out of date. Adding it to the download queue.");
                            FileInfosToDownload.Add(ServerFileInfos[i]);
                        }
                        else
                        {
                            Logger.Log("Updater: File " + ServerFileInfos[i].Filename + " exists in the directory and is up to date.");
                        }
                    }
                    else
                    {
                        Logger.Log("Updater: File " + ServerFileInfos[i].Filename + " not found. Adding it to the download queue.");
                        FileInfosToDownload.Add(ServerFileInfos[i]);
                    }
                }
            }

            UpdateSizeInKb = 0;

            for (int j = 0; j < FileInfosToDownload.Count; j++)
            {
                UpdateSizeInKb += FileInfosToDownload[j].Archived ? FileInfosToDownload[j].ArchiveSize : FileInfosToDownload[j].Size;
            }

            VersionState = VersionState.OUTDATED;
            ManualUpdateRequired = false;
            DoFileIdentifiersUpdatedEvent();
        }

        /// <summary>
        /// Verifies local file version info.
        /// </summary>
        private static void VerifyLocalFileVersions()
        {
            Logger.Log("Verifying local file versions. Count: " + LocalFileInfos.Count);
            for (int i = 0; i < LocalFileInfos.Count; i++)
            {
                UpdaterFileInfo info = LocalFileInfos[i];
                if (!ContainsAnyMask(info.Filename))
                {
                    if (SafePath.GetFile(GamePath, info.Filename).Exists)
                    {
                        string uniqueIdForFile = GetUniqueIdForFile(info.Filename);
                        if (uniqueIdForFile != info.Identifier)
                        {
                            Logger.Log("Invalid unique identifier for " + info.Filename + "!");
                            info.Identifier = uniqueIdForFile;
                        }
                    }
                    else
                    {
                        Logger.Log("File " + info.Filename + " does not exist!");
                        LocalFileInfos.RemoveAt(i);
                        i--;
                    }
                    if (LocalFileInfos.Count > 0)
                        LocalFileCheckProgressChanged?.Invoke(i + 1, LocalFileInfos.Count);
                }
            }
        }

        /// <summary>
        /// Downloads files required for update and starts second-stage updater.
        /// </summary>
        private static void PerformUpdate()
        {
            Logger.Log("Updater: Starting update.");
            VersionState = VersionState.UPDATEINPROGRESS;

            try
            {
                if (!ExecutePreUpdateScript())
                    throw new Exception("Executing preupdateexec failed.");

                VerifyLocalFileVersions();
                VersionCheckHandle();

                if ((string.IsNullOrEmpty(ServerGameVersion) || (ServerGameVersion == "N/A")) || (VersionState != VersionState.OUTDATED))
                    throw new Exception("Update server integrity error.");

                VersionState = VersionState.UPDATEINPROGRESS;

                totalDownloadedKbs = 0;

                if (terminateUpdate)
                {
                    Logger.Log("Updater: Terminating update because of user request.");
                    VersionState = VersionState.OUTDATED;
                    ManualUpdateRequired = false;
                    terminateUpdate = false;
                }
                else
                {
                    using WebClient client = new WebClient
                    {
                        CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore)
                    };
                    client.Headers.Add(HttpRequestHeader.UserAgent, GetUserAgentString());
                    client.DownloadProgressChanged += WebClient_DownloadProgressChanged;
                    client.DownloadFileCompleted += WebClient_DownloadFileCompleted;

                    foreach (UpdaterFileInfo info in FileInfosToDownload)
                    {
                        bool flag = false;
                        int num = 0;

                        if (terminateUpdate)
                        {
                            Logger.Log("Updater: Terminating update because of user request.");
                            VersionState = VersionState.OUTDATED;
                            ManualUpdateRequired = false;
                            terminateUpdate = false;
                            return;
                        }

                        while (true)
                        {
                            currentFilename = info.Archived ? info.Filename + ARCHIVE_FILE_EXTENSION : info.Filename;
                            currentFileSize = info.Archived ? info.ArchiveSize : info.Size;
                            flag = DownloadFile(client, info);

                            if (terminateUpdate)
                            {
                                Logger.Log("Updater: Terminating update because of user request.");
                                VersionState = VersionState.OUTDATED;
                                ManualUpdateRequired = false;
                                terminateUpdate = false;
                                return;
                            }

                            if (flag)
                            {
                                totalDownloadedKbs += info.Archived ? info.ArchiveSize : info.Size;
                                break;
                            }

                            num++;

                            if (num == 2)
                            {
                                Logger.Log("Updater: Too many retries for downloading file " +
                                    (info.Archived ? info.Filename + ARCHIVE_FILE_EXTENSION : info.Filename) + ". Update halted.");

                                throw new Exception("Too many retries for downloading file " +
                                    (info.Archived ? info.Filename + ARCHIVE_FILE_EXTENSION : info.Filename));
                            }
                        }
                    }

                    if (terminateUpdate)
                    {
                        Logger.Log("Updater: Terminating update because of user request.");
                        VersionState = VersionState.OUTDATED;
                        ManualUpdateRequired = false;
                        terminateUpdate = false;
                    }
                    else
                    {
                        Logger.Log("Updater: Downloading files finished - copying from temporary updater directory.");
                        ExecuteAfterUpdateScript();
                        Logger.Log("Updater: Cleaning up.");

                        DirectoryInfo updaterDirectoryInfo = SafePath.GetDirectory(GamePath, "Updater");
                        FileInfo versionFileTemp = SafePath.GetFile(GamePath, FormattableString.Invariant($"{VERSION_FILE}_u"));

                        if (updaterDirectoryInfo.Exists)
                        {
                            versionFileTemp.MoveTo(SafePath.CombineFilePath(GamePath, "Updater", VERSION_FILE));
                        }
                        else
                        {
                            versionFileTemp.MoveTo(SafePath.CombineFilePath(GamePath, VERSION_FILE));
                        }

                        FileInfo themeFileInfo = SafePath.GetFile(GamePath, "Theme_c.ini");

                        if (themeFileInfo.Exists)
                        {
                            Logger.Log("Updater: Theme_c.ini exists -- copying it.");
                            themeFileInfo.CopyTo(SafePath.CombineFilePath(GamePath, "INI", "Theme.ini"), true);
                            Logger.Log("Updater: Theme.ini copied succesfully.");
                        }

                        updaterDirectoryInfo.Refresh();

                        if (updaterDirectoryInfo.Exists)
                        {
                            FileInfo secondStageUpdater = SafePath.GetFile(GamePath, "Updater", "Resources", SECOND_STAGE_UPDATER);
                            FileInfo secondStageUpdaterResource = SafePath.GetFile(ResourcePath, SECOND_STAGE_UPDATER);

                            if (secondStageUpdater.Exists)
                            {
                                DeleteFileAndWait(secondStageUpdaterResource.FullName);
                                secondStageUpdater.MoveTo(secondStageUpdaterResource.FullName);
                            }

                            Logger.Log("Updater: Launching second-stage updater executable " + SECOND_STAGE_UPDATER + ".");

                            using var _ = Process.Start(new ProcessStartInfo
                            {
                                UseShellExecute = false,
                                FileName = secondStageUpdaterResource.FullName,
                                Arguments = CallingExecutableFileName + " \"" + GamePath + "\""
                            });

                            Restart?.Invoke(null, EventArgs.Empty);
                        }
                        else
                        {
                            Logger.Log("Updater: Update completed succesfully.");
                            totalDownloadedKbs = 0;
                            UpdateSizeInKb = 0;
                            CheckLocalFileVersions();
                            ServerGameVersion = "N/A";
                            VersionState = VersionState.UPTODATE;
                            DoUpdateCompleted();

                            if (AreCustomComponentsOutdated())
                                DoCustomComponentsOutdatedEvent();
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Logger.Log("Updater: An error occured during the update. Message: " + exception.Message);
                VersionState = VersionState.UNKNOWN;
                DoOnUpdateFailed(exception);
            }
        }

        /// <summary>
        /// Downloads and handles individual file.
        /// </summary>
        /// <param name="client">Web client instance to use to download the file.</param>
        /// <param name="fileInfo">File info for the file.</param>
        /// <returns>True if successful, otherwise false.</returns>
        private static bool DownloadFile(WebClient client, UpdaterFileInfo fileInfo)
        {
            Logger.Log("Updater: Initiliazing download of file " + fileInfo.Filename);

            UpdateDownloadProgress(0);
            currentDownloadException = null;
            string filename = fileInfo.Filename;
            string prefixPath = "Updater";
            FileInfo decompressedFile = SafePath.GetFile(GamePath, prefixPath, filename);

            try
            {
                string uriString = "";
                int currentUpdateMirrorId = Updater.currentUpdateMirrorIndex;
                string extraExtension = fileInfo.Archived ? ARCHIVE_FILE_EXTENSION : "";
                string fileRelativePath = SafePath.CombineFilePath(prefixPath, FormattableString.Invariant($"{filename}{extraExtension}"));
                uriString = (updateMirrors[currentUpdateMirrorId].URL + filename + extraExtension).Replace(@"\", "/");
                FileInfo downloadFile = SafePath.GetFile(GamePath, fileRelativePath);
                CreatePath(SafePath.CombineFilePath(GamePath, filename));
                CreatePath(downloadFile.FullName);

                if (downloadFile.Exists &&
                    (fileInfo.Archived ? fileInfo.Identifier : fileInfo.ArchiveIdentifier) == GetUniqueIdForFile(fileRelativePath))
                {
                    Logger.Log("Updater: File " + filename + " has already been downloaded, skipping downloading.");
                }
                else
                {
                    Logger.Log("Updater: Downloading file " + filename + extraExtension);
                    client.DownloadFileAsync(new Uri(uriString), downloadFile.FullName);

                    while (client.IsBusy)
                        Thread.Sleep(10);

                    if (currentDownloadException != null)
                        throw currentDownloadException;

                    OnFileDownloadCompleted?.Invoke(fileInfo.Archived ? filename + extraExtension : null);
                    Logger.Log("Updater: Download of file " + filename + extraExtension + " finished - verifying.");

                    if (fileInfo.Archived)
                    {
                        Logger.Log("Updater: File is an archive.");
                        string archiveIdentifier = CheckFileIdentifiers(filename, fileRelativePath, fileInfo.ArchiveIdentifier);

                        if (string.IsNullOrEmpty(archiveIdentifier))
                        {
                            Logger.Log("Updater: Archive " + filename + extraExtension + " is intact. Unpacking...");
                            CompressionHelper.DecompressFile(downloadFile.FullName, decompressedFile.FullName);
                            downloadFile.Delete();
                        }
                        else
                        {
                            Logger.Log("Updater: Downloaded archive " + filename + extraExtension + " has a non-matching identifier: " + archiveIdentifier + " against " + fileInfo.ArchiveIdentifier);
                            DeleteFileAndWait(downloadFile.FullName);

                            return false;
                        }
                    }

                    client.CancelAsync();
                }

                string fileIdentifier = CheckFileIdentifiers(filename, SafePath.CombineFilePath(prefixPath, filename), fileInfo.Identifier);
                if (string.IsNullOrEmpty(fileIdentifier))
                {
                    Logger.Log("Updater: File " + filename + " is intact.");

                    return true;
                }

                Logger.Log("Updater: Downloaded file " + filename + " has a non-matching identifier: " + fileIdentifier + " against " + fileInfo.Identifier);
                DeleteFileAndWait(decompressedFile.FullName);

                return false;
            }
            catch (Exception exception)
            {
                Logger.Log("Updater: An error occurred while downloading file " + filename + ": " + exception.Message);
                DeleteFileAndWait(decompressedFile.FullName);
                client.CancelAsync();

                return false;
            }
        }

        /// <summary>
        /// Updates download progress.
        /// </summary>
        /// <param name="progressPercentage">Progress percentage.</param>
        private static void UpdateDownloadProgress(int progressPercentage)
        {
            double num = currentFileSize * (progressPercentage / 100.0);
            double num2 = totalDownloadedKbs + num;

            int totalPercentage = 0;

            if ((UpdateSizeInKb > 0) && (UpdateSizeInKb < Int32.MaxValue))
                totalPercentage = (int)((num2 / UpdateSizeInKb) * 100.0);

            DownloadProgressChanged(currentFilename, progressPercentage, totalPercentage);
        }

        /// <summary>
        /// Checks if file path contains ignore masks.
        /// </summary>
        /// <param name="filePath">File path to check.</param>
        /// <returns>True if path contains any ignore masks, otherwise false.</returns>
        private static bool ContainsAnyMask(string filePath)
        {
            foreach (string str2 in ignoreMasks)
            {
                if (filePath.ToUpper().Contains(str2.ToUpper()))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Gets keys from INI file section.
        /// </summary>
        /// <param name="iniFile">INI file.</param>
        /// <param name="sectionName">Section name.</param>
        /// <returns>List of keys or empty list if section does not exist or no keys were found.</returns>
        private static List<string> GetKeys(IniFile iniFile, string sectionName)
        {
            List<string> keys = iniFile.GetSectionKeys(sectionName);

            if (keys != null)
                return keys;

            return new List<string>();
        }

        /// <summary>
        /// Attempts to get file identifier for a file.
        /// </summary>
        /// <param name="filePath">File path of file.</param>
        /// <returns>File identifier if successful, otherwise empty string.</returns>
        private static string TryGetUniqueId(string filePath)
        {
            try
            {
                return GetUniqueIdForFile(filePath);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Checks file identifiers to see if file is intact.
        /// </summary>
        /// <param name="fileInfoFilename">Filename in file info.</param>
        /// <param name="localFilename">Filename on system.</param>
        /// <param name="fileInfoIdentifier">Current file identifier.</param>
        /// <returns>File identifier if check is successful, otherwise null.</returns>
        private static string CheckFileIdentifiers(string fileInfoFilename, string localFilename, string fileInfoIdentifier)
        {
            string identifier;
            if (ContainsAnyMask(fileInfoFilename))
                identifier = fileInfoIdentifier;

            else
                identifier = GetUniqueIdForFile(localFilename);

            if (fileInfoIdentifier == identifier)
                return null;
            else
                return identifier;
        }

        #endregion

        #region events_and_delegates

        public static event NoParamEventHandler FileIdentifiersUpdated;

        public static event LocalFileCheckProgressChangedCallback LocalFileCheckProgressChanged;

        public static event NoParamEventHandler OnCustomComponentsOutdated;

        public static event NoParamEventHandler OnLocalFileVersionsChecked;

        public static event NoParamEventHandler OnUpdateCompleted;

        public static event SetExceptionCallback OnUpdateFailed;

        public static event NoParamEventHandler OnVersionStateChanged;

        public static event FileDownloadCompletedEventHandler OnFileDownloadCompleted;

        public static event EventHandler Restart;

        public static event UpdateProgressChangedCallback UpdateProgressChanged;

        public delegate void LocalFileCheckProgressChangedCallback(int checkedFileCount, int totalFileCount);

        public delegate void NoParamEventHandler();

        public delegate void SetExceptionCallback(Exception ex);

        public delegate void UpdateProgressChangedCallback(string currFileName, int currFilePercentage, int totalPercentage);

        public delegate void FileDownloadCompletedEventHandler(string archiveName);

        private static void WebClient_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e) => UpdateDownloadProgress(e.ProgressPercentage);

        private static void WebClient_DownloadFileCompleted(object sender, AsyncCompletedEventArgs e) => currentDownloadException = e.Error;

        private static void DownloadProgressChanged(string currFileName, int currentFilePercentage, int totalPercentage) => UpdateProgressChanged?.Invoke(currFileName, currentFilePercentage, totalPercentage);

        private static void DoCustomComponentsOutdatedEvent() => OnCustomComponentsOutdated?.Invoke();

        private static void DoFileIdentifiersUpdatedEvent()
        {
            Logger.Log("Updater: File identifiers updated.");
            FileIdentifiersUpdated?.Invoke();
        }

        private static void DoOnUpdateFailed(Exception ex) => OnUpdateFailed?.Invoke(ex);

        private static void DoOnVersionStateChanged() => OnVersionStateChanged?.Invoke();

        private static void DoUpdateCompleted() => OnUpdateCompleted?.Invoke();

        #endregion
    }
}