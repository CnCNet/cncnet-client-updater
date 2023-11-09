﻿// Copyright 2023 CnCNet
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.

namespace SecondStageUpdater;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Rampastring.Tools;

internal sealed class Program
{
    private const int MutexTimeoutInSeconds = 30;

    private static ConsoleColor defaultColor;
    private static bool hasHandle;
    private static Mutex clientMutex;

    // e.g. args = new[] { "clientogl.dll", "\"C:\\Game\\\"" };
    private static void Main(string[] args)
    {
        defaultColor = Console.ForegroundColor;

        try
        {
            Write("CnCNet Client Second-Stage Updater", true, ConsoleColor.Green);
            Write(string.Empty);

            if (args.Length < 2 || string.IsNullOrEmpty(args[0]) || string.IsNullOrEmpty(args[1]) || !SafePath.GetDirectory(args[1].Replace("\"", null, StringComparison.OrdinalIgnoreCase)).Exists)
            {
                Write("Invalid arguments given!", true, ConsoleColor.Red);
                Write("Usage: <client_executable_name> <base_directory>");
                Write(string.Empty);
                Exit(false);
            }
            else
            {
                FileInfo clientExecutable = SafePath.GetFile(args[0]);
                DirectoryInfo baseDirectory = SafePath.GetDirectory(args[1].Replace("\"", null, StringComparison.OrdinalIgnoreCase));
                DirectoryInfo resourceDirectory = SafePath.GetDirectory(baseDirectory.FullName, "Resources");
                FileInfo logFile = SafePath.GetFile(SafePath.CombineFilePath(baseDirectory.FullName, "Client", "SecondStageUpdater.log"));

                if (logFile.Exists)
                    logFile.Delete();

                Logger.Initialize(logFile.DirectoryName, logFile.Name);
                Logger.WriteLogFile = true;
                Logger.WriteToConsole = false;
                Logger.Log("CnCNet Client Second-Stage Updater");
                Logger.Log("Version: " + Assembly.GetAssembly(typeof(Program)).GetName().Version);
                Write("Base directory: " + baseDirectory.FullName);
                Write($"Waiting for the client ({clientExecutable.Name}) to exit..");

                string clientMutexId = FormattableString.Invariant($"Global{Guid.Parse("1CC9F8E7-9F69-4BBC-B045-E734204027A9")}");

                clientMutex = new(false, clientMutexId, out _);

                try
                {
                    hasHandle = clientMutex.WaitOne(TimeSpan.FromSeconds(MutexTimeoutInSeconds), false);
                }
                catch (AbandonedMutexException)
                {
                    hasHandle = true;
                }

                if (!hasHandle)
                {
                    Write($"Timeout while waiting for the client ({clientExecutable.Name}) to exit!", true, ConsoleColor.Red);
                    Exit(false);
                }

                // This is occasionally necessary to prevent DLLs from being locked at the time that this update is attempting to overwrite them
                Thread.Sleep(1000);

                DirectoryInfo updaterDirectory = SafePath.GetDirectory(baseDirectory.FullName, "Updater");

                if (!updaterDirectory.Exists)
                {
                    Write($"{updaterDirectory.Name} directory does not exist!", true, ConsoleColor.Red);
                    Exit(false);
                }

                Write("Updating files.", true, ConsoleColor.Green);

                IEnumerable<FileInfo> files = updaterDirectory.EnumerateFiles("*", SearchOption.AllDirectories);
                FileInfo executableFile = SafePath.GetFile(Assembly.GetExecutingAssembly().Location);
                FileInfo relativeExecutableFile = SafePath.GetFile(executableFile.FullName[baseDirectory.FullName.Length..]);
                const string versionFileName = "version";

                Write($"{nameof(SecondStageUpdater)}: {relativeExecutableFile}");

                foreach (FileInfo fileInfo in files)
                {
                    FileInfo relativeFileInfo = SafePath.GetFile(fileInfo.FullName[updaterDirectory.FullName.Length..]);
                    AssemblyName[] assemblies = Assembly.LoadFrom(executableFile.FullName).GetReferencedAssemblies();

                    if (relativeFileInfo.ToString()[..^relativeFileInfo.Extension.Length].Equals(relativeExecutableFile.ToString()[..^relativeExecutableFile.Extension.Length], StringComparison.OrdinalIgnoreCase)
                        || relativeFileInfo.ToString()[..^relativeFileInfo.Extension.Length].Equals(SafePath.CombineFilePath("Resources", Path.GetFileNameWithoutExtension(relativeExecutableFile.Name)), StringComparison.OrdinalIgnoreCase))
                    {
                        Write($"Skipping {nameof(SecondStageUpdater)} file {relativeFileInfo}");
                    }
                    else if (assemblies.Any(q => relativeFileInfo.ToString()[..^relativeFileInfo.Extension.Length].Equals(q.Name, StringComparison.OrdinalIgnoreCase))
                        || assemblies.Any(q => relativeFileInfo.ToString()[..^relativeFileInfo.Extension.Length].Equals(SafePath.CombineFilePath("Resources", q.Name), StringComparison.OrdinalIgnoreCase)))
                    {
                        Write($"Skipping {nameof(SecondStageUpdater)} dependency {relativeFileInfo}");
                    }
                    else if (relativeFileInfo.ToString().Equals(versionFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        Write($"Skipping {relativeFileInfo}");
                    }
                    else
                    {
                        try
                        {
                            FileInfo copiedFile = SafePath.GetFile(baseDirectory.FullName, relativeFileInfo.ToString());

                            Write($"Updating {relativeFileInfo}");
                            fileInfo.CopyTo(copiedFile.FullName, true);
                        }
                        catch (Exception ex)
                        {
                            Write($"Updating file failed! Returned error message: {ex}", true, ConsoleColor.Yellow);
                            Write("If the problem persists, try to move the content of the \"Updater\" directory to the main directory manually or contact the staff for support.");
                            Exit(false);
                        }
                    }
                }

                FileInfo versionFile = SafePath.GetFile(updaterDirectory.FullName, versionFileName);

                if (versionFile.Exists)
                {
                    FileInfo destinationFile = SafePath.GetFile(baseDirectory.FullName, versionFile.Name);
                    FileInfo relativeFileInfo = SafePath.GetFile(destinationFile.FullName[baseDirectory.FullName.Length..]);

                    Write($"Updating {relativeFileInfo}");
                    versionFile.CopyTo(destinationFile.FullName, true);
                }

                Write("Files successfully updated. Starting launcher..", true, ConsoleColor.Green);
                string launcherExe = string.Empty;

                try
                {
                    Write("Checking ClientDefinitions.ini for launcher executable filename.");

                    string[] lines = File.ReadAllLines(SafePath.CombineFilePath(resourceDirectory.FullName, "ClientDefinitions.ini"));
                    string launcherPropertyName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "LauncherExe" : "UnixLauncherExe";
                    string line = lines.Single(q => q.Trim().StartsWith(launcherPropertyName, StringComparison.OrdinalIgnoreCase) && q.Contains('=', StringComparison.OrdinalIgnoreCase));
                    int commentStart = line.IndexOf(';', StringComparison.OrdinalIgnoreCase);

                    if (commentStart >= 0)
                        line = line[..commentStart];

                    launcherExe = line.Split('=')[1].Trim();
                }
                catch (Exception ex)
                {
                    Write($"Failed to read ClientDefinitions.ini: {ex}", true, ConsoleColor.Yellow);
                }

                FileInfo launcherExeFile = SafePath.GetFile(baseDirectory.FullName, launcherExe);

                if (launcherExeFile.Exists)
                {
                    Write("Launcher executable found: " + launcherExe, true, ConsoleColor.Green);

#pragma warning disable SA1312 // Variable names should begin with lower-case letter
                    using var _ = Process.Start(new ProcessStartInfo
                    {
                        FileName = launcherExeFile.FullName
                    });
#pragma warning restore SA1312 // Variable names should begin with lower-case letter
                }
                else
                {
                    Write("No suitable launcher executable found! Client will not automatically start after updater closes.", true, ConsoleColor.Yellow);
                    Exit(false);
                }
            }

            Exit(true);
        }
        catch (Exception ex)
        {
            Write("An error occurred during the Launcher Updater's operation.", true, ConsoleColor.Red);
            Write($"Returned error was: {ex}");
            Write(string.Empty);
            Write("If you were updating a game, please try again. If the problem continues, contact the staff for support.");
            Exit(false);
        }
    }

    private static void Exit(bool success)
    {
        if (hasHandle)
        {
            clientMutex.ReleaseMutex();
            clientMutex.Dispose();
        }

        if (!success)
        {
            Write("Press any key to exit.");
            Console.ReadKey();
            Environment.Exit(1);
        }
    }

    private static void Write(string text, bool logToFile = true, ConsoleColor? color = null)
    {
        Console.ForegroundColor = color ?? defaultColor;
        Console.WriteLine(text);

        if (logToFile)
            Logger.Log(text);
    }
}