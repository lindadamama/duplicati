// Copyright (C) 2025, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using Duplicati.Library.Common.IO;

namespace Duplicati.Library.Utility
{
    /// <summary>
    /// This class represents a temporary file that will be automatically deleted when disposed
    /// </summary>
    public class TempFile : IDisposable
    {
        /// <summary>
        /// The prefix applied to all temporary files
        /// </summary>
        public static string APPLICATION_PREFIX = Utility.getEntryAssembly().FullName.Substring(0, 3).ToLower(System.Globalization.CultureInfo.InvariantCulture) + "-";

        private string m_path;
        private bool m_protect;
        private bool m_disposed = false;

#if DEBUG
        //In debug mode, we track the creation of temporary files, and encode the generating method into the name
        private static readonly object m_lock = new object();
        private static readonly Dictionary<string, System.Diagnostics.StackTrace> m_fileTrace = new Dictionary<string, System.Diagnostics.StackTrace>();

        public static System.Diagnostics.StackTrace GetStackTraceForTempFile(string filename)
        {
            lock (m_lock)
                if (m_fileTrace.ContainsKey(filename))
                    return m_fileTrace[filename];
                else
                    return null;
        }

        private static string GenerateUniqueName()
        {
            var st = new System.Diagnostics.StackTrace();
            foreach (var f in st.GetFrames())
            {
                var asm = f.GetMethod()?.DeclaringType?.Assembly;
                if (asm != null && asm != typeof(TempFile).Assembly)
                {
                    var n = string.Format("{0}_{1}_{2}_{3}", f.GetMethod().DeclaringType.FullName, f.GetMethod().Name, Library.Utility.Utility.SerializeDateTime(DateTime.UtcNow), Guid.NewGuid().ToString().Substring(0, 8));
                    if (n.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                        n = string.Format("{0}_{1}_{2}_{3}", f.GetMethod().DeclaringType.Name, f.GetMethod().Name, Library.Utility.Utility.SerializeDateTime(DateTime.UtcNow), Guid.NewGuid().ToString().Substring(0, 8));
                    if (n.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0)
                    {
                        lock (m_lock)
                            m_fileTrace.Add(n, st);
                        return n;
                    }
                }
            }

            var s = Guid.NewGuid().ToString();
            lock (m_lock)
                m_fileTrace.Add(s, st);
            return s;
        }
#else
        private static string GenerateUniqueName()
        {
            return APPLICATION_PREFIX + Guid.NewGuid().ToString();
        }
#endif
        /// <summary>
        /// The name of the temp file
        /// </summary>
        public string Name => m_path;

        /// <summary>
        /// Gets all temporary files found in the current tempdir, that matches the application prefix
        /// </summary>
        /// <returns>The application temp files.</returns>
        private static IEnumerable<string> GetApplicationTempFiles()
        {
#if DEBUG
            return SystemIO.IO_OS.GetFiles(TempFolder.SystemTempPath, "Duplicati*");
#else
            return SystemIO.IO_OS.GetFiles(TempFolder.SystemTempPath, APPLICATION_PREFIX + "*");
#endif
        }

        /// <summary>
        /// Removes all old temporary files for this application.
        /// </summary>
        /// <param name="errorcallback">An optional callback method for logging errors</param>
        public static void RemoveOldApplicationTempFiles(Action<string, Exception> errorcallback = null)
        {
#if DEBUG
            var expires = TimeSpan.FromHours(3);
#else
            var expires = TimeSpan.FromDays(30);
#endif
            foreach (string e in GetApplicationTempFiles())
            {
                try
                {
                    if (DateTime.UtcNow > (System.IO.File.GetLastWriteTimeUtc(e) + expires))
                    {
                        System.IO.File.Delete(e);
                    }
                }
                catch (Exception ex)
                {
                    errorcallback?.Invoke(e, ex);
                }
            }
        }

        public TempFile()
            : this(System.IO.Path.Combine(TempFolder.SystemTempPath, GenerateUniqueName()))
        {
        }

        private TempFile(string path)
        {
            m_path = path;
            m_protect = false;
            if (!System.IO.File.Exists(m_path))
                using (System.IO.File.Create(m_path))
                { /*Dispose it immediately*/ }
        }

        /// <summary>
        /// A value indicating if the file is protected, meaning that it will not be deleted when the instance is disposed.
        /// Defaults to false, meaning that the file will be deleted when disposed.
        /// </summary>
        public bool Protected
        {
            get { return m_protect; }
            set { m_protect = value; }
        }

        public static implicit operator string(TempFile path)
        {
            return path == null ? null : path.m_path;
        }

        public static implicit operator TempFile(string path)
        {
            return new TempFile(path);
        }

        public static TempFile WrapExistingFile(string path)
        {
            return new TempFile(path);
        }

        public static TempFile CreateInFolder(string path, bool autocreatefolder)
        {
            if (autocreatefolder && !System.IO.Directory.Exists(path))
                System.IO.Directory.CreateDirectory(path);

            return new TempFile(System.IO.Path.Combine(path, GenerateUniqueName()));
        }

        public static TempFile CreateWithPrefix(string prefix)
        {
            return new TempFile(System.IO.Path.Combine(TempFolder.SystemTempPath, prefix + GenerateUniqueName()));
        }

        protected void Dispose(bool disposing)
        {
            m_disposed = true;

            if (disposing)
                GC.SuppressFinalize(this);

            try
            {
                if (!m_protect && m_path != null && System.IO.File.Exists(m_path))
                    System.IO.File.Delete(m_path);
                m_path = null;
            }
            catch
            {
            }
        }

        #region IDisposable Members

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion

        ~TempFile()
        {
            if (!m_disposed)
                Logging.Log.WriteWarningMessage("TempFile", "Finalizer", null, "TempFile was not disposed, cleaning up");

            Dispose(false);
        }

        /// <summary>
        /// Swaps two instances of temporary files, equivalent to renaming the files but requires no IO
        /// </summary>
        /// <param name="tf">The temp file to swap with</param>
        public void Swap(TempFile tf)
        {
            string p = m_path;
            m_path = tf.m_path;
            tf.m_path = p;
        }
    }
}
