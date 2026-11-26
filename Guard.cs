// What keeps an unexpected error from costing the user anything: a small error log, and the one way the app writes
// its own files. An error that reaches the top of the app is written to %AppData%\HdrSwitch\error.log and the app
// carries on (see Program.Main). A file is written next to itself as <name>.tmp first and only then swapped in, so a
// crash, a full disk or a power cut mid-write leaves the old file whole instead of an empty one; a file that can't be
// written (read-only, locked, no permission) is left as it is and the reason goes to the log.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HdrSwitch
{
    static class Guard
    {
        /// <summary>Where errors are written. The checks point it at a folder of their own.</summary>
        internal static string LogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HdrSwitch", "error.log");
        /// <summary>Above this size the log keeps only its newest half.</summary>
        internal const int MaxLog = 256 * 1024;

        static readonly object sync = new object();
        static readonly Encoding Utf8 = new UTF8Encoding(false);
        static string lastKey;     // the entry written last, and when: the same error over and over (a paint that fails
        static DateTime lastAt;    // every frame) is written once a minute, not sixty times a second

        /// <summary>Writes an error to the log: when, where it happened, its type, message and stack. Never throws.</summary>
        internal static void Log(string where, Exception e)
        {
            Write(where, e == null ? "(no details)" : e.ToString());
        }

        /// <summary>Writes a line of text to the log. Never throws.</summary>
        internal static void Log(string what)
        {
            Write(what, null);
        }

        static void Write(string where, string details)
        {
            try
            {
                lock (sync)
                {
                    string key = where + "\n" + details;
                    var now = DateTime.Now;
                    if (key == lastKey && (now - lastAt).TotalSeconds < 60) return;
                    lastKey = key;
                    lastAt = now;
                    Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
                    Cap(LogFile, MaxLog);
                    File.AppendAllText(LogFile, now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + where + "\r\n"
                        + (details == null ? "" : details + "\r\n") + "\r\n", Utf8);
                }
            }
            catch (Exception) { }   // nowhere left to report it
        }

        /// <summary>A file over max bytes keeps its newest half, from the start of a line.</summary>
        internal static void Cap(string path, int max)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= max) return;
            byte[] all = File.ReadAllBytes(path);
            int from = Array.IndexOf(all, (byte)'\n', all.Length - max / 2);
            from = from < 0 ? all.Length : from + 1;
            var keep = new byte[all.Length - from];
            Array.Copy(all, from, keep, 0, keep.Length);
            Swap(path, tmp => File.WriteAllBytes(tmp, keep));   // not through Save: that would log, from inside the log
        }

        /// <summary>Saves lines (UTF-8, as the app has always written its lists). False if it couldn't: the old file is untouched.</summary>
        internal static bool Save(string path, IEnumerable<string> lines)
        {
            return Save(path, tmp => File.WriteAllLines(tmp, lines, Encoding.UTF8));
        }

        /// <summary>Saves a short text (a .state file). False if it couldn't: the old file is untouched.</summary>
        internal static bool Save(string path, string text)
        {
            return Save(path, tmp => File.WriteAllText(tmp, text));
        }

        static bool Save(string path, Action<string> write)
        {
            try
            {
                // A file made read-only was made so on purpose: it is left alone, the same on every version of Windows.
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                {
                    Log("Not saved, the file is read-only: " + path);
                    return false;
                }
                Swap(path, write);
                return true;
            }
            catch (IOException e) { Log("Couldn't save " + path, e); }
            catch (UnauthorizedAccessException e) { Log("Couldn't save " + path, e); }
            catch (System.Security.SecurityException e) { Log("Couldn't save " + path, e); }
            return false;
        }

        /// <summary>
        /// Writes <path>.tmp, then puts it in the file's place in one step: File.Replace over an existing file (Windows'
        /// ReplaceFile, which keeps the file's attributes and permissions), File.Move for a new one. If writing the .tmp
        /// fails, the file itself was never opened. A .tmp left by a failure is removed when it can be.
        /// </summary>
        static void Swap(string path, Action<string> write)
        {
            string tmp = path + ".tmp";
            try
            {
                write(tmp);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
                throw;
            }
        }

        /// <summary>Deletes one of the app's files. True if it is gone (or never was); false, and logged, if it couldn't be.</summary>
        internal static bool Delete(string path)
        {
            try
            {
                File.Delete(path);   // no error if it doesn't exist
                return true;
            }
            catch (IOException e) { Log("Couldn't delete " + path, e); }
            catch (UnauthorizedAccessException e) { Log("Couldn't delete " + path, e); }
            catch (System.Security.SecurityException e) { Log("Couldn't delete " + path, e); }
            return false;
        }
    }
}
