/**
 * The MIT License
 * 
 * Authors:
 *   Ben Hoyt, oyster.com
 *   Thomas Dohmke, Bit Stadium GmbH, hockeyapp.net
 *
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 * 
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE. 
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.UI.Popups;
using Windows.UI.Xaml;

// Note: there's a big issue with Application.UnhandledException -- it doesn't catch exceptions thrown
// from event handlers marked as async. This is a known issue that Microsoft may or may not be working on. See:
// http://stackoverflow.com/questions/12344357/unhandled-exception-handler-not-called-for-metro-winrt-ui-async-void-event-han
// http://social.msdn.microsoft.com/Forums/en-US/winappswithcsharp/thread/bea154b0-08b0-4fdc-be31-058d9f5d1c4e
// UnhandledException also seems not to get called from background threads (Task.Run)
namespace HockeyApp
{
    public class CrashHandler
    {
        private const string LogsFolderName = "CrashLogs";
        private const string Platform = "Windows 8";
        private const string PostUrlFormat = "https://rink.hockeyapp.net/api/2/apps/{0}/crashes";
        private const string SdkName = "HockeySDKWindows8";
        private const string SdkVersion = "1.0";
        private static readonly TimeSpan KeepLogsAge = TimeSpan.FromDays(2);

        // Apparently device manufacturer/model info is not available to Windows Store apps, see:
        // http://social.msdn.microsoft.com/Forums/da-DK/winappswithnativecode/thread/dab68444-d609-48b8-9291-cbe152234fc7
        private const string DeviceManufacturer = "Unknown";
        private const string DeviceModel = "Unknown";

        private static string Identifier;
        private static Application Application;
        private static bool AskBeforeSending;
        private static string CrashFolderPath;

        public static string UserId { get; set; }

        // Call 'await Initialize(this, "HOCKEYAPP_IDENTIFIER")' at the end of your App's OnLaunched method
        public static async Task<List<string>> Initialize(Application application, string identifier, bool askBeforeSending = true)
        {
            if (Application != null)
            {
                throw new InvalidOperationException("CrashHandler was already initialized");
            }

            Application = application;
            Identifier = identifier;
            AskBeforeSending = askBeforeSending;

            var localFolder = ApplicationData.Current.LocalFolder;
            var crashFolder = await localFolder.CreateFolderAsync(LogsFolderName, CreationCollisionOption.OpenIfExists);
            CrashFolderPath = crashFolder.Path;

            Application.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            var unsentCrashLogs = await GetUnsentRawCrashLogs();
            await HandleCrashes();
            return unsentCrashLogs;
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
        {
            try
            {
                SaveException(args.Message, args.Exception);
            }
            catch
            {
                // Ignore all uncaught exceptions while handling an exception, otherwise
                // nasty infinite loops and other bad things might happen
            }
        }

        static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs args)
        {
            try
            {
                SaveException(args.Exception.Message, args.Exception);
            }
            catch
            {
                // Ignore all uncaught exceptions while handling an exception, otherwise
                // nasty infinite loops and other bad things might happen
            }
        }

        private static string GetLog(String message, Exception exception)
        {
            var builder = new StringBuilder();
            var assembly = Application.GetType().GetTypeInfo().Assembly;
            var version = Package.Current.Id.Version;

            builder.AppendFormat("Package: {0}\n", assembly.GetCustomAttribute<AssemblyProductAttribute>().Product);
            builder.AppendFormat("Version: {0}.{1}.{2}.{3}\n", version.Major, version.Minor, version.Build, version.Revision);
            builder.AppendFormat("Platform: {0}\n", Platform);
            builder.AppendFormat("Manufacturer: {0}\n", DeviceManufacturer);
            builder.AppendFormat("Model: {0}\n", DeviceModel);
            builder.AppendFormat("Date: {0}\n", DateTime.UtcNow.ToString("o"));
            builder.Append("\n");
            builder.Append(exception.ToString());
            builder.Append("\n");
            builder.Append(message);

            return builder.ToString().Trim();
        }

        public static void SaveException(String message, Exception exception)
        {
            var log = GetLog(message, exception);
            var filename = String.Format("crash{0}.log", DateTime.UtcNow.ToString("s").Replace(":", "-"));
            var filepath = Path.Combine(CrashFolderPath, filename);
            SyncHelpers.WriteFile(filepath, log, Encoding.UTF8);
        }

        private static async Task HandleCrashes()
        {
            try
            {
                var crashFolder = await StorageFolder.GetFolderFromPathAsync(CrashFolderPath);
                var allFiles = await crashFolder.GetFilesAsync();
                var crashFiles = allFiles.Where(f => f.Name.StartsWith("crash") && f.Name.EndsWith(".log")).ToList();
                if (crashFiles.Count == 0)
                {
                    // No crash logs, do nothing
                    return;
                }

                bool shouldSend = true;
                if (AskBeforeSending)
                {
                    var dialog = new MessageDialog("The app quit unexpectedly. Would you like to send information about this to the developer to help them fix the problem?",
                                                   "Send crash data?");
                    dialog.Commands.Add(new UICommand("Send", null, "send"));
                    dialog.Commands.Add(new UICommand("Don't send", null, "dontsend"));
                    dialog.DefaultCommandIndex = 0;
                    dialog.CancelCommandIndex = 1;
                    var command = await dialog.ShowAsync();
                    shouldSend = (string)command.Id == "send";
                }

                if (shouldSend)
                {
                    await SendCrashes(crashFiles);
                }
                else
                {
                    await DeleteCrashes(crashFiles);
                }
            }
            catch
            {
                // Ignore all uncaught exceptions while sending/deleting crash logs
            }
        }

        // Note: this POSTs using the same API as the HockeySDK-Windows Phone project, not
        // the publicly-documented "Post Custom Crashes" API (the latter uses multipart file
        // uploads, so is slightly harder to use). Thomas from HockeyApp said "both work fine".
        private static async Task SendCrashes(List<StorageFile> files)
        {
            var filesToDelete = new List<StorageFile>();

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", String.Format("{0}/{1}", SdkName, SdkVersion));

                foreach (var file in files)
                {
                    var fileAge = DateTime.UtcNow - file.DateCreated;
                    if (fileAge > KeepLogsAge)
                    {
                        // Log file too old, delete instead of sending
                        filesToDelete.Add(file);
                        continue;
                    }

                    var parameters = new Dictionary<string, string>();
                    parameters["raw"] = await FileIO.ReadTextAsync(file);
                    parameters["sdk"] = SdkName;
                    parameters["sdk_version"] = SdkVersion;
                    if (!String.IsNullOrEmpty(UserId))
                    {
                        parameters["userID"] = UserId;
                    }
                    var content = new FormUrlEncodedContent(parameters);

                    try
                    {
                        var response = await client.PostAsync(String.Format(PostUrlFormat, Identifier), content);
                        if (response.StatusCode == HttpStatusCode.Created)
                        {
                            // Sent successfully, delete log file
                            filesToDelete.Add(file);
                        }
                    }
                    catch (HttpRequestException)
                    {
                        // Don't delete file if there's an HTTP error, keep it around for KeepLogsAge
                    }
                }
            }

            await DeleteCrashes(filesToDelete);
        }

        private static async Task DeleteCrashes(List<StorageFile> files)
        {
            foreach (var file in files)
            {
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
        }

        // Gets the unsent raw crash logs.
        public static async Task<List<string>> GetUnsentRawCrashLogs()
        {
            var ret = new List<string>();
            var crashFolder = await StorageFolder.GetFolderFromPathAsync(CrashFolderPath);
            var allFiles = await crashFolder.GetFilesAsync();
            var crashFiles = allFiles.Where(f => f.Name.StartsWith("crash") && f.Name.EndsWith(".log")).ToList();
            foreach (var file in crashFiles)
            {
                ret.Add(await FileIO.ReadTextAsync(file));
            }
            return ret;
        }

        // SaveLog is called from UnhandledException, so it needs to be synchronous. Using P/Invoke
        // with fopen/fwrite/fclose is the easiest way I've found to write a file synchronously using
        // allowed Windows Store APIs.
        private class SyncHelpers
        {
            [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            static extern IntPtr fopen(string filename, string mode);
            [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            static extern int fclose(IntPtr file);
            [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            static extern IntPtr fwrite(byte[] buffer, IntPtr size, IntPtr number, IntPtr file);

            public static void WriteFile(string path, string text, Encoding encoding)
            {
                var bytes = encoding.GetBytes(text);
                var fp = fopen(path, "wb");
                if (fp != IntPtr.Zero)
                {
                    fwrite(bytes, new IntPtr(1), new IntPtr(bytes.Count()), fp);
                    fclose(fp);
                }
            }
        }
    }
}
