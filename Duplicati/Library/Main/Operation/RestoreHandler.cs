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
using System.Linq;
using Duplicati.Library.Interface;
using Duplicati.Library.Common.IO;
using Duplicati.Library.Main.Database;
using Duplicati.Library.Main.Volumes;
using Duplicati.Library.Utility;
using CoCoL;
using System.Threading.Tasks;
using System.Threading;

namespace Duplicati.Library.Main.Operation
{
    internal class RestoreHandler
    {
        /// <summary>
        /// The tag used for logging
        /// </summary>
        private static readonly string LOGTAG = Logging.Log.LogTagFromType<RestoreHandler>();

        private readonly Options m_options;
        private byte[] m_blockbuffer;
        private readonly RestoreResults m_result;
        private static readonly string DIRSEP = Util.DirectorySeparatorString;

        public RestoreHandler(Options options, RestoreResults result)
        {
            m_options = options;
            m_result = result;
        }

        /// <summary>
        /// Gets the compression module by parsing the filename
        /// </summary>
        /// <param name="filename">The filename to parse</param>
        /// <returns>The compression module</returns>
        public static string GetCompressionModule(string filename)
        {
            var tmp = VolumeBase.ParseFilename(filename);
            if (tmp == null)
                throw new UserInformationException(string.Format("Unable to parse filename to valid entry: {0}", filename), "FailedToParseRemoteName");

            return tmp.CompressionModule;
        }

        public static RecreateDatabaseHandler.NumberedFilterFilelistDelegate FilterNumberedFilelist(DateTime time, long[] versions, bool singleTimeMatch = false)
        {
            if (time.Kind == DateTimeKind.Unspecified)
                throw new Exception("Unspecified datetime instance, must be either local or UTC");

            // Make sure the resolution is the same (i.e. no milliseconds)
            if (time.Ticks > 0)
                time = Library.Utility.Utility.DeserializeDateTime(Library.Utility.Utility.SerializeDateTime(time)).ToUniversalTime();

            return
                _lst =>
                {
                    // Unwrap, so we do not query the remote storage twice
                    var lst = (from n in _lst
                               where n.FileType == RemoteVolumeType.Files
                               orderby n.Time descending
                               select n).ToArray();

                    var numbers = lst.Zip(Enumerable.Range(0, lst.Length), (a, b) => new KeyValuePair<long, IParsedVolume>(b, a)).ToList();

                    if (time.Ticks > 0 && versions != null && versions.Length > 0)
                        return from n in numbers
                               where (singleTimeMatch ? n.Value.Time == time : n.Value.Time <= time) && versions.Contains(n.Key)
                               select n;
                    else if (time.Ticks > 0)
                        return from n in numbers
                               where (singleTimeMatch ? n.Value.Time == time : n.Value.Time <= time)
                               select n;
                    else if (versions != null && versions.Length > 0)
                        return from n in numbers
                               where versions.Contains(n.Key)
                               select n;
                    else
                        return numbers;
                };
        }

        public async Task RunAsync(string[] paths, IBackendManager backendManager, Library.Utility.IFilter filter)
        {
            m_result.OperationProgressUpdater.UpdatePhase(OperationPhase.Restore_Begin);

            // If we have both target paths and a filter, combine them into a single filter
            filter = JoinedFilterExpression.Join(new FilterExpression(paths), filter);

            LocalRestoreDatabase db = null;
            TempFile tmpdb = null;
            try
            {
                if (!m_options.NoLocalDb && SystemIO.IO_OS.FileExists(m_options.Dbpath))
                {
                    db = await LocalRestoreDatabase.CreateAsync(m_options.Dbpath, null, m_result.TaskControl.ProgressToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    Logging.Log.WriteInformationMessage(LOGTAG, "NoLocalDatabase", "No local database, building a temporary database");
                    tmpdb = new TempFile();
                    RecreateDatabaseHandler.NumberedFilterFilelistDelegate filelistfilter = FilterNumberedFilelist(m_options.Time, m_options.Version);
                    db = await LocalRestoreDatabase.CreateAsync(tmpdb, null, m_result.TaskControl.ProgressToken)
                        .ConfigureAwait(false);
                    m_result.RecreateDatabaseResults = new RecreateDatabaseResults(m_result);
                    using (new Logging.Timer(LOGTAG, "RecreateTempDbForRestore", "Recreate temporary database for restore"))
                        await new RecreateDatabaseHandler(m_options, (RecreateDatabaseResults)m_result.RecreateDatabaseResults)
                            .DoRunAsync(backendManager, db, false, filter, filelistfilter, null)
                            .ConfigureAwait(false);

                    if (!m_options.SkipMetadata)
                        ApplyStoredMetadata(m_options, new RestoreHandlerMetadataStorage());

                    //If we have --version set, we need to adjust, as the db has only the required versions
                    //TODO: Bit of a hack to set options that way
                    if (m_options.Version != null && m_options.Version.Length > 0)
                        m_options.RawOptions["version"] = string.Join(",", Enumerable.Range(0, m_options.Version.Length).Select(x => x.ToString()));
                }

                if (m_options.RestoreLegacy)
                    await DoRunAsync(backendManager, db, filter, m_result.TaskControl.ProgressToken).ConfigureAwait(false);
                else
                    await DoRunNewAsync(backendManager, db, filter, m_result.TaskControl.ProgressToken).ConfigureAwait(false);
            }
            finally
            {
                if (db != null)
                    await db.DisposeAsync().ConfigureAwait(false);
                tmpdb?.Dispose();
            }
        }

        private static async Task PatchWithBlocklist(LocalRestoreDatabase database, BlockVolumeReader blocks, Options options, RestoreResults result, byte[] blockbuffer, RestoreHandlerMetadataStorage metadatastorage, CancellationToken cancellationToken)
        {
            var blocksize = options.Blocksize;
            var updateCounter = 0L;
            var fullblockverification = options.FullBlockVerification;

            using var blockhasher = HashFactory.CreateHasher(options.BlockHashAlgorithm);
            await using var blockmarker = await database.CreateBlockMarkerAsync(cancellationToken).ConfigureAwait(false);
            await using var volumekeeper = await database.GetMissingBlockData(blocks, options.Blocksize, cancellationToken).ConfigureAwait(false);
            await foreach (var restorelist in volumekeeper.FilesWithMissingBlocks(cancellationToken).ConfigureAwait(false))
            {
                var targetpath = restorelist.Path;

                if (options.Dryrun)
                {
                    Logging.Log.WriteDryrunMessage(LOGTAG, "WouldPatchFile", "Would patch file with remote data: {0}", targetpath);
                }
                else
                {
                    Logging.Log.WriteVerboseMessage(LOGTAG, "PatchingFile", "Patching file with remote data: {0}", targetpath);

                    try
                    {
                        var folderpath = SystemIO.IO_OS.PathGetDirectoryName(targetpath);
                        if (!options.Dryrun && !SystemIO.IO_OS.DirectoryExists(folderpath))
                        {
                            Logging.Log.WriteWarningMessage(LOGTAG, "CreateMissingFolder", null, "Creating missing folder {0} for  file {1}", folderpath, targetpath);
                            SystemIO.IO_OS.DirectoryCreate(folderpath);
                        }

                        // TODO: Much faster if we iterate the volume and checks what blocks are used,
                        // because the compressors usually like sequential reading
                        using (var file = SystemIO.IO_OS.FileOpenWrite(targetpath))
                            await foreach (var targetblock in restorelist.Blocks(cancellationToken).ConfigureAwait(false))
                            {
                                file.Position = targetblock.Offset;
                                var size = blocks.ReadBlock(targetblock.Key, blockbuffer);
                                if (targetblock.Size == size)
                                {
                                    var valid = !fullblockverification;
                                    if (!valid)
                                    {
                                        var key = Convert.ToBase64String(blockhasher.ComputeHash(blockbuffer, 0, size));
                                        if (targetblock.Key == key)
                                            valid = true;
                                        else
                                            Logging.Log.WriteWarningMessage(LOGTAG, "InvalidBlock", null, "Invalid block detected for {0}, expected hash: {1}, actual hash: {2}", targetpath, targetblock.Key, key);
                                    }

                                    if (valid)
                                    {
                                        file.Write(blockbuffer, 0, size);
                                        await blockmarker
                                            .SetBlockRestored(restorelist.FileID, targetblock.Offset / blocksize, targetblock.Key, size, false, cancellationToken)
                                            .ConfigureAwait(false);
                                    }
                                }
                                else
                                {
                                    Logging.Log.WriteWarningMessage(LOGTAG, "WrongBlockSize", null, "Block with hash {0} should have size {1} but has size {2}", targetblock.Key, targetblock.Size, size);
                                }
                            }

                        if ((++updateCounter) % 20 == 0)
                            await blockmarker
                                .UpdateProcessed(result.OperationProgressUpdater, cancellationToken)
                                .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logging.Log.WriteWarningMessage(LOGTAG, "PatchFailed", ex, "Failed to patch file: \"{0}\", message: {1}, message: {1}", targetpath, ex.Message);
                        if (options.UnittestMode)
                            throw;
                    }
                }
            }

            if (!options.SkipMetadata)
            {
                await foreach (var restoremetadata in volumekeeper.MetadataWithMissingBlocks(cancellationToken).ConfigureAwait(false))
                {
                    var targetpath = restoremetadata.Path;
                    Logging.Log.WriteVerboseMessage(LOGTAG, "RecordingMetadata", "Recording metadata from remote data: {0}", targetpath);

                    try
                    {
                        // TODO: When we support multi-block metadata this needs to deal with it
                        using (var ms = new System.IO.MemoryStream())
                        {
                            await foreach (var targetblock in restoremetadata.Blocks(cancellationToken).ConfigureAwait(false))
                            {
                                ms.Position = targetblock.Offset;
                                var size = blocks.ReadBlock(targetblock.Key, blockbuffer);
                                if (targetblock.Size == size)
                                {
                                    ms.Write(blockbuffer, 0, size);
                                    await blockmarker
                                        .SetBlockRestored(restoremetadata.FileID, targetblock.Offset / blocksize, targetblock.Key, size, true, cancellationToken)
                                        .ConfigureAwait(false);
                                }
                            }

                            ms.Position = 0;
                            metadatastorage.Add(targetpath, ms);
                            //blockmarker.RecordMetadata(restoremetadata.FileID, ms);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logging.Log.WriteWarningMessage(LOGTAG, "MetatdataRecordFailed", ex, "Failed to record metadata for file: \"{0}\", message: {1}", targetpath, ex.Message);
                        if (options.UnittestMode)
                            throw;
                    }
                }
            }
            await blockmarker
                .UpdateProcessed(result.OperationProgressUpdater, cancellationToken)
                .ConfigureAwait(false);
            await blockmarker.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        private static void ApplyStoredMetadata(Options options, RestoreHandlerMetadataStorage metadatastorage)
        {
            foreach (var metainfo in metadatastorage.Records)
            {
                var targetpath = metainfo.Key;

                if (options.Dryrun)
                {
                    Logging.Log.WriteDryrunMessage(LOGTAG, "WouldPatchMetadata", "Would patch metadata with remote data: {0}", targetpath);
                }
                else
                {
                    Logging.Log.WriteVerboseMessage(LOGTAG, "PatchingMetadata", "Patching metadata with remote data: {0}", targetpath);
                    try
                    {
                        var folderpath = Duplicati.Library.Utility.Utility.GetParent(targetpath, false);
                        if (!options.Dryrun && !SystemIO.IO_OS.DirectoryExists(folderpath))
                        {
                            Logging.Log.WriteWarningMessage(LOGTAG, "CreateMissingFolder", null, "Creating missing folder {0} for target {1}", folderpath, targetpath);
                            SystemIO.IO_OS.DirectoryCreate(folderpath);
                        }

                        ApplyMetadata(targetpath, metainfo.Value, options.RestorePermissions, options.RestoreSymlinkMetadata, options.Dryrun);
                    }
                    catch (Exception ex)
                    {
                        Logging.Log.WriteWarningMessage(LOGTAG, "MetadataWriteFailed", ex, "Failed to apply metadata to file: \"{0}\", message: {1}", targetpath, ex.Message);
                        if (options.UnittestMode)
                            throw;
                    }
                }
            }
        }

        /// <summary>
        /// Perform the restore operation.
        /// This is the new implementation, which utilizes a CSP network of processes to perform the restore.
        /// </summary>
        /// <param name="database">The database containing information about the restore.</param>
        /// <param name="filter">The filter of which files to restore.</param>
        private async Task DoRunNewAsync(IBackendManager backendManager, LocalRestoreDatabase database, Library.Utility.IFilter filter, CancellationToken cancellationToken)
        {
            // Perform initial setup
            await Utility.UpdateOptionsFromDb(database, m_options, cancellationToken)
                .ConfigureAwait(false);
            await Utility.VerifyOptionsAndUpdateDatabase(database, m_options, cancellationToken)
                .ConfigureAwait(false);

            // Verify the backend if necessary
            if (!m_options.NoBackendverification)
            {
                m_result.OperationProgressUpdater.UpdatePhase(OperationPhase.Restore_PreRestoreVerify);
                await FilelistProcessor.VerifyRemoteList(backendManager, m_options, database, m_result.BackendWriter, latestVolumesOnly: false, verifyMode: FilelistProcessor.VerifyMode.VerifyOnly, cancellationToken).ConfigureAwait(false);
            }

            // Prepare the block and file list and create the directory structure
            m_result.OperationProgressUpdater.UpdatePhase(OperationPhase.Restore_CreateFileList);
            using (new Logging.Timer(LOGTAG, "PrepareBlockList", "PrepareBlockList"))
                await PrepareBlockAndFileList(database, m_options, filter, m_result)
                    .ConfigureAwait(false);
            m_result.OperationProgressUpdater.UpdatePhase(OperationPhase.Restore_CreateTargetFolders);
            using (new Logging.Timer(LOGTAG, "CreateDirectory", "CreateDirectory"))
                await CreateDirectoryStructure(database, m_options, m_result).ConfigureAwait(false);

            // At this point, there should be no more writes to the database, so we have to unlock the database:
            await database.Transaction
                .CommitAsync("CommitBeforeRestore", token: cancellationToken)
                .ConfigureAwait(false);

            using var setup_log_timer = new Logging.Timer(LOGTAG, "RestoreNetworkSetup", "RestoreNetworkSetup");
            // Create the channels between BlockManager and FileProcessor
            Restore.Channels.BufferSize = m_options.RestoreChannelBufferSize;
            var fileprocessor_requests = Enumerable.Range(0, m_options.RestoreFileProcessors).Select(_ => ChannelManager.CreateChannel<Restore.BlockRequest>(buffersize: Restore.Channels.BufferSize)).ToArray();
            var fileprocessor_responses = Enumerable.Range(0, m_options.RestoreFileProcessors).Select(_ => ChannelManager.CreateChannel<Task<byte[]>>(buffersize: Restore.Channels.BufferSize)).ToArray();

            // Create the process network
            Restore.Channels channels = new();
            var filelister = Restore.FileLister.Run(channels, database, m_options, m_result);
            Restore.FileProcessor.file_processors_restoring_files = m_options.RestoreFileProcessors;
            var fileprocessors = Enumerable.Range(0, m_options.RestoreFileProcessors).Select(i => Restore.FileProcessor.Run(channels, database, fileprocessor_requests[i], fileprocessor_responses[i], m_options, m_result)).ToArray();
            var blockmanager = Restore.BlockManager.Run(channels, database, fileprocessor_requests, fileprocessor_responses, m_options, m_result);
            var volumecache = Restore.VolumeManager.Run(channels, m_options);
            var volumedownloaders = Enumerable.Range(0, m_options.RestoreVolumeDownloaders).Select(i => Restore.VolumeDownloader.Run(channels, database, backendManager, m_options, m_result)).ToArray();
            var volumedecryptors = Enumerable.Range(0, m_options.RestoreVolumeDecryptors).Select(i => Restore.VolumeDecryptor.Run(channels, backendManager, m_options)).ToArray();
            var volumedecompressors = Enumerable.Range(0, m_options.RestoreVolumeDecompressors).Select(i => Restore.VolumeDecompressor.Run(channels, m_options)).ToArray();

            setup_log_timer.Dispose();

            // Wait for the network to complete
            Task[] all =
                [
                    filelister,
                    ..fileprocessors,
                    blockmanager,
                    volumecache,
                    ..volumedownloaders,
                    ..volumedecryptors,
                    ..volumedecompressors
                ];

            // Start the progress updater
            using (new Logging.Timer(LOGTAG, "RestoreNetworkWait", "RestoreNetworkWait"))
            using (var kill_updater = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var updater = Task.Run(async () =>
                {
                    while (!kill_updater.Token.IsCancellationRequested)
                    {
                        m_result.OperationProgressUpdater.UpdatefilesProcessed(m_result.RestoredFiles, m_result.SizeOfRestoredFiles);
                        await Task.Delay(1000, kill_updater.Token).ConfigureAwait(false);
                    }
                }, kill_updater.Token);

                await Task.WhenAll(all).ConfigureAwait(false);
                kill_updater.Cancel();
            }

            await database.Transaction
                .CommitAsync("CommitAfterRestore", token: cancellationToken)
                .ConfigureAwait(false);

            await database.DisposePoolAsync().ConfigureAwait(false);

            if (!await m_result.TaskControl.ProgressRendevouz().ConfigureAwait(false))
                return;

            m_result.OperationProgressUpdater.UpdatePhase(OperationPhase.Restore_PostRestoreVerify);

            // If any errors occurred, log them
            if (m_result.BrokenRemoteFiles.Count > 0 || m_result.BrokenLocalFiles.Count > 0)
            {
                var nl = Environment.NewLine;
                int maxN = 10;
                long remoteFirstN = Math.Min(maxN, m_result.BrokenRemoteFiles.Count);
                string remoteFirst = remoteFirstN < m_result.BrokenRemoteFiles.Count ? $"first {maxN} " : string.Empty;
                long localFirstN = Math.Min(maxN, m_result.BrokenLocalFiles.Count);
                string localFirst = localFirstN < m_result.BrokenLocalFiles.Count ? $"first {maxN} " : string.Empty;

                string remoteMessage = m_result.BrokenRemoteFiles.Count > 0 ? $"Failed to download {m_result.BrokenRemoteFiles.Count} remote files." : string.Empty;
                string remoteList = m_result.BrokenRemoteFiles.Count > 0 ? $"The following {remoteFirst}remote files failed to download, which may be the cause:{nl}{string.Join(nl, m_result.BrokenRemoteFiles.Take(maxN))}{nl}" : string.Empty;
                string localMessage = m_result.BrokenLocalFiles.Count > 0 ? $"Failed to restore {m_result.BrokenLocalFiles.Count} local files." : string.Empty;
                string localList = m_result.BrokenLocalFiles.Count > 0 ? $"The following {localFirst}local files failed to restore:{nl}{string.Join(nl, m_result.BrokenLocalFiles.Take(maxN))}{nl}" : string.Empty;

                Logging.Log.WriteErrorMessage(LOGTAG, "RestoreFailures", null, $"{remoteMessage}{nl}{localMessage}{nl}{remoteList}{nl}{localList}");
            }
            else if (m_result.RestoredFiles == 0)
                Logging.Log.WriteWarningMessage(LOGTAG, "NoFilesRestored", null, "Restore completed without errors but no files were restored");

            // Drop the temp tables
            await database.DropRestoreTable(cancellationToken).ConfigureAwait(false);
            await backendManager.WaitForEmptyAsync(database, m_result.TaskControl.ProgressToken).ConfigureAwait(false);

            // Report that the restore is complete
            m_result.OperationProgressUpdater.UpdatePhase(OperationPhase.Restore_Complete);
            m_result.EndTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Perform the restore operation.
        /// This is the legacy implementation, which performs the restore in a single thread. Kept as in case the new implementation fails.
        /// </summary>
        /// <param name="database">The database containing information about the restore.</param>
        /// <param name="filter">The filter of which files to restore.</param>
        private async Task DoRunAsync(IBackendManager backendManager, LocalRestoreDatabase database, Library.Utility.IFilter filter, CancellationToken cancellationToken)
        {
            //In this case, we check that the remote storage fits with the database.
            //We can then query the database and find the blocks that we need to do the restore
            //using (var database = new LocalRestoreDatabase(dbparent))
            using (var metadatastorage = new RestoreHandlerMetadataStorage())
            {
                await Utility.UpdateOptionsFromDb(database, m_options, cancellationToken)
                    .ConfigureAwait(false);
                await Utility.VerifyOptionsAndUpdateDatabase(database, m_options, cancellationToken)
                    .ConfigureAwait(false);
                m_blockbuffer = new byte[m_options.Blocksize];

                if (!m_options.NoBackendverification)
                {
                    m_result.OperationProgressUpdater.UpdatePhase(OperationPhase.Restore_PreRestoreVerify);
                    await FilelistProcessor.VerifyRemoteList(backendManager, m_options, database, m_result.BackendWriter, latestVolumesOnly: false, verifyMode: FilelistProcessor.VerifyMode.VerifyOnly, cancellationToken).ConfigureAwait(false);
                }

                //Figure out what files are to be patched, and what blocks are needed
                m_result.OperationProgressUpdater.UpdatePhase(OperationPhase.Restore_CreateFileList);
                using (new Logging.Timer(LOGTAG, "PrepareBlockList", "PrepareBlockList"))
                    await PrepareBlockAndFileList(database, m_options, filter, m_result)
                        .ConfigureAwait(false);

                //Make the entire output setup
                m_result.OperationProgressUpdater.UpdatePhase(OperationPhase.Restore_CreateTargetFolders);
                using (new Logging.Timer(LOGTAG, "CreateDirectory", "CreateDirectory"))
                    await CreateDirectoryStructure(database, m_options, m_result)
                        .ConfigureAwait(false);

                //If we are patching an existing target folder, do not touch stuff that is already updated
                m_result.OperationProgressUpdater.UpdatePhase(OperationPhase.Restore_ScanForExistingFiles);
                using (var blockhasher = HashFactory.CreateHasher(m_options.BlockHashAlgorithm))
                using (var filehasher = HashFactory.CreateHasher(m_options.FileHashAlgorithm))
                using (new Logging.Timer(LOGTAG, "ScanForExistingTargetBlocks", "ScanForExistingTargetBlocks"))
                    await ScanForExistingTargetBlocks(database, m_blockbuffer, blockhasher, filehasher, m_options, m_result).ConfigureAwait(false);

                //Look for existing blocks in the original source files only
                if (m_options.UseLocalBlocks && !string.IsNullOrEmpty(m_options.Restorepath))
                {
                    using (var blockhasher = HashFactory.CreateHasher(m_options.BlockHashAlgorithm))
                    using (new Logging.Timer(LOGTAG, "ScanForExistingSourceBlocksFast", "ScanForExistingSourceBlocksFast"))
                    {
                        m_result.OperationProgressUpdater.UpdatePhase(OperationPhase.Restore_ScanForLocalBlocks);
                        await ScanForExistingSourceBlocksFast(database, m_options, m_blockbuffer, blockhasher, m_result).ConfigureAwait(false);
                    }
                }

                if (!await m_result.TaskControl.ProgressRendevouz().ConfigureAwait(false))
                {
                    await backendManager.WaitForEmptyAsync(database, cancellationToken).ConfigureAwait(false);
                    return;
                }

                // If other local files already have the blocks we want, we use them instead of downloading
                if (m_options.UseLocalBlocks)
                {
                    m_result.OperationProgressUpdater.UpdatePhase(OperationPhase.Restore_PatchWithLocalBlocks);
                    using (var blockhasher = HashFactory.CreateHasher(m_options.BlockHashAlgorithm))
                    using (new Logging.Timer(LOGTAG, "PatchWithLocalBlocks", "PatchWithLocalBlocks"))
                        await ScanForExistingSourceBlocks(database, m_options, m_blockbuffer, blockhasher, m_result, metadatastorage).ConfigureAwait(false);
                }

                if (!await m_result.TaskControl.ProgressRendevouz().ConfigureAwait(false))
                {
                    await backendManager.WaitForEmptyAsync(database, cancellationToken).ConfigureAwait(false);
                    return;
                }

                // Fill BLOCKS with remote sources
                List<IRemoteVolume> volumes;
                using (new Logging.Timer(LOGTAG, "GetMissingVolumes", "GetMissingVolumes"))
                    volumes = await database
                        .GetMissingVolumes(cancellationToken)
                        .ToListAsync(cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                if (volumes.Count > 0)
                {
                    Logging.Log.WriteInformationMessage(LOGTAG, "RemoteFileCount", "{0} remote files are required to restore", volumes.Count);
                    m_result.OperationProgressUpdater.UpdatePhase(OperationPhase.Restore_DownloadingRemoteFiles);
                }

                var brokenFiles = new List<string>();

                using (new Logging.Timer(LOGTAG, "PatchWithBlocklist", "PatchWithBlocklist"))
                    await foreach (var (tmpfile, _, _, name) in backendManager.GetFilesOverlappedAsync(volumes, cancellationToken).ConfigureAwait(false))
                    {
                        try
                        {
                            if (!await m_result.TaskControl.ProgressRendevouz().ConfigureAwait(false))
                            {
                                await backendManager.WaitForEmptyAsync(database, cancellationToken).ConfigureAwait(false);
                                return;
                            }

                            using (tmpfile)
                            using (var blocks = new BlockVolumeReader(GetCompressionModule(name), tmpfile, m_options))
                                await PatchWithBlocklist(database, blocks, m_options, m_result, m_blockbuffer, metadatastorage, cancellationToken)
                                    .ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            brokenFiles.Add(name);
                            Logging.Log.WriteErrorMessage(LOGTAG, "PatchingFailed", ex, "Failed to patch with remote file: \"{0}\", message: {1}", name, ex.Message);
                            if (ex.IsAbortException())
                                throw;
                        }
                    }

                var fileErrors = 0L;

                // Restore empty files. They might not have any blocks so don't appear in any volume.
                await foreach (var file in database.GetFilesToRestore(true, cancellationToken).Where(item => item.Length == 0).ConfigureAwait(false))
                {
                    Logging.Log.WriteVerboseMessage(LOGTAG, "RestoreEmptyFile", "Restoring empty file \"{0}\"", file.Path);

                    try
                    {
                        SystemIO.IO_OS.DirectoryCreate(SystemIO.IO_OS.PathGetDirectoryName(file.Path));
                        // Just create the file and close it right away, empty statement is intentional.
                        using (SystemIO.IO_OS.FileCreate(file.Path))
                        {
                        }
                    }
                    catch (Exception ex)
                    {
                        fileErrors++;
                        Logging.Log.WriteErrorMessage(LOGTAG, "RestoreFileFailed", ex, "Failed to restore empty file: \"{0}\". Error message was: {1}", file.Path, ex.Message);
                        if (ex.IsAbortException())
                            throw;
                    }
                }

                // Enforcing the length of files is now already done during ScanForExistingTargetBlocks
                // and thus not necessary anymore.

                // Apply metadata
                if (!m_options.SkipMetadata)
                    ApplyStoredMetadata(m_options, metadatastorage);

                if (!await m_result.TaskControl.ProgressRendevouz().ConfigureAwait(false))
                    return;

                m_result.OperationProgressUpdater.UpdatePhase(OperationPhase.Restore_PostRestoreVerify);


                if (m_options.PerformRestoredFileVerification)
                {
                    // After all blocks in the files are restored, verify the file hash
                    using (var filehasher = HashFactory.CreateHasher(m_options.FileHashAlgorithm))
                    using (new Logging.Timer(LOGTAG, "RestoreVerification", "RestoreVerification"))
                        await foreach (var file in database.GetFilesToRestore(true, cancellationToken).ConfigureAwait(false))
                        {
                            try
                            {
                                if (!await m_result.TaskControl.ProgressRendevouz().ConfigureAwait(false))
                                {
                                    await backendManager.WaitForEmptyAsync(database, cancellationToken).ConfigureAwait(false);
                                    return;
                                }

                                Logging.Log.WriteVerboseMessage(LOGTAG, "TestFileIntegrity", "Testing restored file integrity: {0}", file.Path);

                                string key;
                                long size;
                                using (var fs = SystemIO.IO_OS.FileOpenRead(file.Path))
                                {
                                    size = fs.Length;
                                    key = Convert.ToBase64String(filehasher.ComputeHash(fs));
                                }

                                if (key != file.Hash)
                                    throw new Exception(string.Format("Failed to restore file: \"{0}\". File hash is {1}, expected hash is {2}", file.Path, key, file.Hash));
                                m_result.RestoredFiles++;
                                m_result.SizeOfRestoredFiles += size;
                            }
                            catch (Exception ex)
                            {
                                fileErrors++;
                                Logging.Log.WriteErrorMessage(LOGTAG, "RestoreFileFailed", ex, "Failed to restore file: \"{0}\". Error message was: {1}", file.Path, ex.Message);
                                if (ex.IsAbortException())
                                    throw;
                            }
                        }
                }

                if (fileErrors > 0 && brokenFiles.Count > 0)
                    Logging.Log.WriteInformationMessage(LOGTAG, "RestoreFailures", "Failed to restore {0} files, additionally the following files failed to download, which may be the cause:{1}{2}", fileErrors, Environment.NewLine, string.Join(Environment.NewLine, brokenFiles));
                else if (fileErrors > 0)
                    Logging.Log.WriteInformationMessage(LOGTAG, "RestoreFailures", "Failed to restore {0} files", fileErrors);
                else if (m_result.RestoredFiles == 0)
                    Logging.Log.WriteWarningMessage(LOGTAG, "NoFilesRestored", null, "Restore completed without errors but no files were restored");

                // Drop the temp tables
                await database.DropRestoreTable(cancellationToken).ConfigureAwait(false);
                await backendManager.WaitForEmptyAsync(database, cancellationToken).ConfigureAwait(false);
            }

            m_result.OperationProgressUpdater.UpdatePhase(OperationPhase.Restore_Complete);
            m_result.EndTime = DateTime.UtcNow;
        }

        public static bool ApplyMetadata(string path, System.IO.Stream stream, bool restorePermissions, bool restoreSymlinkMetadata, bool dryrun)
        {
            // TODO This has been modified to return a bool indicating if anything was written to properly report the number of files restored. The legacy restore doesn't check this, which produces an error in the CI where it reports that no files have been restored, even though one have. It's in Duplicati/UnitTests/SymLinkTests.cs the test SymLinkTests.SymLinkExists() that fails on the very last assert that there are 0 warnings.
            using (var tr = new System.IO.StreamReader(stream))
            using (var jr = new Newtonsoft.Json.JsonTextReader(tr))
            {
                var metadata = new Newtonsoft.Json.JsonSerializer().Deserialize<Dictionary<string, string>>(jr);
                string k;
                long t;
                System.IO.FileAttributes fa;
                var wrote_something = false;

                // If this is dry-run, we stop after having deserialized the metadata
                if (dryrun)
                    return wrote_something;

                var isDirTarget = path.EndsWith(DIRSEP, StringComparison.Ordinal);
                var targetpath = isDirTarget ? path.Substring(0, path.Length - 1) : path;

                // Make the symlink first, otherwise we cannot apply metadata to it
                if (metadata.TryGetValue("CoreSymlinkTarget", out k))
                {
                    // Check if the target exists, and overwrite it if it does.
                    if (SystemIO.IO_OS.FileExists(targetpath))
                    {
                        SystemIO.IO_OS.FileDelete(targetpath);
                    }
                    else if (SystemIO.IO_OS.DirectoryExists(targetpath))
                    {
                        SystemIO.IO_OS.DirectoryDelete(targetpath, false);
                    }
                    SystemIO.IO_OS.CreateSymlink(targetpath, k, isDirTarget);
                    wrote_something = true;
                }
                // If the target is a folder, make sure we create it first
                else if (isDirTarget && !SystemIO.IO_OS.DirectoryExists(targetpath))
                    SystemIO.IO_OS.DirectoryCreate(targetpath);

                // Avoid setting restoring symlink metadata, as that writes the symlink target, not the symlink itself
                if (!restoreSymlinkMetadata && Snapshots.SnapshotUtility.IsSymlink(SystemIO.IO_OS, targetpath))
                {
                    Logging.Log.WriteVerboseMessage(LOGTAG, "no-symlink-metadata-restored", "Not applying metadata to symlink: {0}", targetpath);
                    return wrote_something;
                }

                if (metadata.TryGetValue("CoreLastWritetime", out k) && long.TryParse(k, out t))
                {
                    if (isDirTarget)
                        SystemIO.IO_OS.DirectorySetLastWriteTimeUtc(targetpath, new DateTime(t, DateTimeKind.Utc));
                    else
                        SystemIO.IO_OS.FileSetLastWriteTimeUtc(targetpath, new DateTime(t, DateTimeKind.Utc));
                }

                if (metadata.TryGetValue("CoreCreatetime", out k) && long.TryParse(k, out t))
                {
                    if (isDirTarget)
                        SystemIO.IO_OS.DirectorySetCreationTimeUtc(targetpath, new DateTime(t, DateTimeKind.Utc));
                    else
                        SystemIO.IO_OS.FileSetCreationTimeUtc(targetpath, new DateTime(t, DateTimeKind.Utc));
                }

                if (metadata.TryGetValue("CoreAttributes", out k) && Enum.TryParse(k, true, out fa))
                    SystemIO.IO_OS.SetFileAttributes(targetpath, fa);

                SystemIO.IO_OS.SetMetadata(path, metadata, restorePermissions);
                return wrote_something;
            }
        }

        private static async Task ScanForExistingSourceBlocksFast(LocalRestoreDatabase database, Options options, byte[] blockbuffer, System.Security.Cryptography.HashAlgorithm hasher, RestoreResults result)
        {
            // Fill BLOCKS with data from known local source files
            await using var blockmarker = await database.CreateBlockMarkerAsync(result.TaskControl.ProgressToken).ConfigureAwait(false);
            var updateCount = 0L;
            await foreach (var entry in database.GetFilesAndSourceBlocksFast(options.Blocksize, result.TaskControl.ProgressToken).ConfigureAwait(false))
            {
                var targetpath = entry.TargetPath;
                var targetfileid = entry.TargetFileID;
                var sourcepath = entry.SourcePath;
                var patched = false;

                try
                {
                    if (SystemIO.IO_OS.FileExists(sourcepath))
                    {
                        var folderpath = SystemIO.IO_OS.PathGetDirectoryName(targetpath);
                        if (!options.Dryrun && !SystemIO.IO_OS.DirectoryExists(folderpath))
                        {
                            Logging.Log.WriteWarningMessage(LOGTAG, "CreateMissingFolder", null, "Creating missing folder {0} for  file {1}", folderpath, targetpath);
                            SystemIO.IO_OS.DirectoryCreate(folderpath);
                        }

                        using (var targetstream = options.Dryrun ? null : SystemIO.IO_OS.FileOpenWrite(targetpath))
                        {
                            try
                            {
                                using var sourcestream = SystemIO.IO_OS.FileOpenRead(sourcepath);
                                await foreach (var block in entry.Blocks(result.TaskControl.ProgressToken).ConfigureAwait(false))
                                {
                                    if (!await result.TaskControl.ProgressRendevouz().ConfigureAwait(false))
                                        return;

                                    //TODO: Handle metadata

                                    if (sourcestream.Length > block.Offset)
                                    {
                                        sourcestream.Position = block.Offset;

                                        int size = Library.Utility.Utility.ForceStreamRead(sourcestream, blockbuffer, blockbuffer.Length);
                                        if (size == block.Size)
                                        {
                                            var key = Convert.ToBase64String(hasher.ComputeHash(blockbuffer, 0, size));
                                            if (key == block.Hash)
                                            {
                                                patched = true;
                                                if (!options.Dryrun)
                                                {
                                                    targetstream.Position = block.Offset;
                                                    targetstream.Write(blockbuffer, 0, size);
                                                }

                                                await blockmarker.SetBlockRestored(targetfileid, block.Index, key, block.Size, false, result.TaskControl.ProgressToken)
                                                    .ConfigureAwait(false);
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logging.Log.WriteWarningMessage(LOGTAG, "PatchingFileLocalFailed", ex, "Failed to patch file: \"{0}\" with data from local file \"{1}\", message: {2}", targetpath, sourcepath, ex.Message);
                                if (ex.IsAbortException())
                                    throw;
                            }
                        }

                        if ((++updateCount) % 20 == 0)
                        {
                            await blockmarker
                                .UpdateProcessed(result.OperationProgressUpdater, result.TaskControl.ProgressToken)
                                .ConfigureAwait(false);
                            if (!await result.TaskControl.ProgressRendevouz().ConfigureAwait(false))
                                return;
                        }

                    }
                    else
                    {
                        Logging.Log.WriteVerboseMessage(LOGTAG, "LocalSourceMissing", "Local source file not found: {0}", sourcepath);
                    }
                }
                catch (Exception ex)
                {
                    Logging.Log.WriteWarningMessage(LOGTAG, "PatchingFileLocalFailed", ex, "Failed to patch file: \"{0}\" with local data, message: {1}", targetpath, ex.Message);
                    if (ex.IsAbortException())
                        throw;
                    if (options.UnittestMode)
                        throw;
                }

                if (patched)
                    Logging.Log.WriteVerboseMessage(LOGTAG, "FilePatchedWithLocal", "Target file is patched with some local data: {0}", targetpath);
                else
                    Logging.Log.WriteVerboseMessage(LOGTAG, "FilePatchedWithLocal", "Target file is not patched any local data: {0}", targetpath);

                if (patched && options.Dryrun)
                    Logging.Log.WriteDryrunMessage(LOGTAG, "WouldPatchWithLocal", "Would patch file with local data: {0}", targetpath);
            }

            await blockmarker
                .UpdateProcessed(result.OperationProgressUpdater, result.TaskControl.ProgressToken)
                .ConfigureAwait(false);
            await blockmarker.CommitAsync(result.TaskControl.ProgressToken).ConfigureAwait(false);
        }

        private static async Task ScanForExistingSourceBlocks(LocalRestoreDatabase database, Options options, byte[] blockbuffer, System.Security.Cryptography.HashAlgorithm hasher, RestoreResults result, RestoreHandlerMetadataStorage metadatastorage)
        {
            // Fill BLOCKS with data from known local source files
            await using var blockmarker = await database.CreateBlockMarkerAsync(result.TaskControl.ProgressToken).ConfigureAwait(false);
            var updateCount = 0L;
            await foreach (var restorelist in database.GetFilesAndSourceBlocks(options.SkipMetadata, options.Blocksize, result.TaskControl.ProgressToken).ConfigureAwait(false))
            {
                var targetpath = restorelist.TargetPath;
                var targetfileid = restorelist.TargetFileID;
                var patched = false;
                try
                {
                    if (!await result.TaskControl.ProgressRendevouz().ConfigureAwait(false))
                        return;

                    var folderpath = SystemIO.IO_OS.PathGetDirectoryName(targetpath);
                    if (!options.Dryrun && !SystemIO.IO_OS.DirectoryExists(folderpath))
                    {
                        Logging.Log.WriteWarningMessage(LOGTAG, "CreateMissingFolder", null, "Creating missing folder {0} for  file {1}", folderpath, targetpath);
                        SystemIO.IO_OS.DirectoryCreate(folderpath);
                    }

                    using (var file = options.Dryrun ? null : SystemIO.IO_OS.FileOpenWrite(targetpath))
                        await foreach (var targetblock in restorelist.Blocks(result.TaskControl.ProgressToken).ConfigureAwait(false))
                        {
                            await foreach (var source in targetblock.BlockSources(result.TaskControl.ProgressToken).ConfigureAwait(false))
                            {
                                try
                                {
                                    if (!await result.TaskControl.ProgressRendevouz().ConfigureAwait(false))
                                        return;

                                    if (SystemIO.IO_OS.FileExists(source.Path))
                                    {
                                        if (source.IsMetadata)
                                        {
                                            // TODO: Handle this by reconstructing
                                            // metadata from file and checking the hash

                                            continue;
                                        }
                                        else
                                        {
                                            using var sourcefile = SystemIO.IO_OS.FileOpenRead(source.Path);
                                            sourcefile.Position = source.Offset;
                                            int size = Library.Utility.Utility.ForceStreamRead(sourcefile, blockbuffer, blockbuffer.Length);
                                            if (size == targetblock.Size)
                                            {
                                                var key = Convert.ToBase64String(hasher.ComputeHash(blockbuffer, 0, size));
                                                if (key == targetblock.Hash)
                                                {
                                                    if (!options.Dryrun)
                                                    {
                                                        if (targetblock.IsMetadata)
                                                            metadatastorage.Add(targetpath, new System.IO.MemoryStream(blockbuffer, 0, size));
                                                        else
                                                        {
                                                            file.Position = targetblock.Offset;
                                                            file.Write(blockbuffer, 0, size);
                                                        }
                                                    }

                                                    await blockmarker
                                                        .SetBlockRestored(targetfileid, targetblock.Index, key, targetblock.Size, false, result.TaskControl.ProgressToken)
                                                        .ConfigureAwait(false);
                                                    patched = true;
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logging.Log.WriteWarningMessage(LOGTAG, "PatchingFileLocalFailed", ex, "Failed to patch file: \"{0}\" with data from local file \"{1}\", message: {2}", targetpath, source.Path, ex.Message);
                                    if (ex.IsAbortException())
                                        throw;
                                }
                            }
                        }

                    if ((++updateCount) % 20 == 0)
                        await blockmarker
                            .UpdateProcessed(result.OperationProgressUpdater, result.TaskControl.ProgressToken)
                            .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logging.Log.WriteWarningMessage(LOGTAG, "PatchingFileLocalFailed", ex, "Failed to patch file: \"{0}\" with local data, message: {1}", targetpath, ex.Message);
                    if (options.UnittestMode)
                        throw;
                }

                if (patched)
                    Logging.Log.WriteVerboseMessage(LOGTAG, "FilePatchedWithLocal", "Target file is patched with some local data: {0}", targetpath);
                else
                    Logging.Log.WriteVerboseMessage(LOGTAG, "FilePatchedWithLocal", "Target file is not patched any local data: {0}", targetpath);

                if (patched && options.Dryrun)
                    Logging.Log.WriteDryrunMessage(LOGTAG, "WouldPatchWithLocal", string.Format("Would patch file with local data: {0}", targetpath));
            }

            await blockmarker
                .UpdateProcessed(result.OperationProgressUpdater, result.TaskControl.ProgressToken)
                .ConfigureAwait(false);
            await blockmarker.CommitAsync(result.TaskControl.ProgressToken).ConfigureAwait(false);
        }

        private static async Task PrepareBlockAndFileList(LocalRestoreDatabase database, Options options, Library.Utility.IFilter filter, RestoreResults result)
        {
            // Create a temporary table FILES by selecting the files from fileset that matches a specific operation id
            // Delete all entries from the temp table that are excluded by the filter(s)
            using (new Logging.Timer(LOGTAG, "PrepareRestoreFileList", "PrepareRestoreFileList"))
            {
                var c = await database
                    .PrepareRestoreFilelist(options.Time, options.Version, filter, result.TaskControl.ProgressToken)
                    .ConfigureAwait(false);
                result.OperationProgressUpdater.UpdatefileCount(c.Item1, c.Item2, true);
            }

            using (new Logging.Timer(LOGTAG, "SetTargetPaths", "SetTargetPaths"))
                if (!string.IsNullOrEmpty(options.Restorepath))
                {
                    // Find the largest common prefix
                    var largest_prefix = options.DontCompressRestorePaths
                        ? "" :
                        await database.GetLargestPrefix(result.TaskControl.ProgressToken).ConfigureAwait(false);

                    Logging.Log.WriteVerboseMessage(LOGTAG, "MappingRestorePath", "Mapping restore path prefix to \"{0}\" to \"{1}\"", largest_prefix, Util.AppendDirSeparator(options.Restorepath));

                    // Set the target paths, special care with C:\ and /
                    await database
                        .SetTargetPaths(largest_prefix, Util.AppendDirSeparator(options.Restorepath), result.TaskControl.ProgressToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await database.SetTargetPaths("", "", result.TaskControl.ProgressToken).ConfigureAwait(false);
                }

            // Create a temporary table BLOCKS that lists all blocks that needs to be recovered
            using (new Logging.Timer(LOGTAG, "FindMissingBlocks", "FindMissingBlocks"))
                await database
                    .FindMissingBlocks(options.SkipMetadata, result.TaskControl.ProgressToken)
                    .ConfigureAwait(false);

            // Create temporary tables and triggers that automatically track progress
            using (new Logging.Timer(LOGTAG, "CreateProgressTracker", "CreateProgressTracker"))
                await database
                    .CreateProgressTracker(false, result.TaskControl.ProgressToken)
                    .ConfigureAwait(false);

        }

        private static async Task CreateDirectoryStructure(LocalRestoreDatabase database, Options options, RestoreResults result)
        {
            // This part is not protected by try/catch as we need the target folder to exist
            if (!string.IsNullOrEmpty(options.Restorepath))
                if (!SystemIO.IO_OS.DirectoryExists(options.Restorepath))
                {
                    Logging.Log.WriteVerboseMessage(LOGTAG, "CreateFolder", "Creating folder: {0}", options.Restorepath);

                    if (options.Dryrun)
                        Logging.Log.WriteDryrunMessage(LOGTAG, "WouldCreateFolder", "Would create folder: {0}", options.Restorepath);
                    else
                        SystemIO.IO_OS.DirectoryCreate(options.Restorepath);
                }

            await foreach (var folder in database.GetTargetFolders(result.TaskControl.ProgressToken).ConfigureAwait(false))
            {
                try
                {
                    if (!await result.TaskControl.ProgressRendevouz().ConfigureAwait(false))
                        return;

                    if (!SystemIO.IO_OS.DirectoryExists(folder))
                    {
                        result.RestoredFolders++;

                        Logging.Log.WriteVerboseMessage(LOGTAG, "CreateFolder", "Creating folder: {0}", folder);

                        if (options.Dryrun)
                            Logging.Log.WriteDryrunMessage(LOGTAG, "WouldCreateFolder", "Would create folder: {0}", folder);
                        else
                            SystemIO.IO_OS.DirectoryCreate(folder);
                    }
                }
                catch (Exception ex)
                {
                    Logging.Log.WriteWarningMessage(LOGTAG, "FolderCreateFailed", ex, "Failed to create folder: \"{0}\", message: {1}", folder, ex.Message);
                    if (options.UnittestMode)
                        throw;
                }
            }
        }

        private static async Task ScanForExistingTargetBlocks(LocalRestoreDatabase database, byte[] blockbuffer, System.Security.Cryptography.HashAlgorithm blockhasher, System.Security.Cryptography.HashAlgorithm filehasher, Options options, RestoreResults result)
        {
            // Scan existing files for existing BLOCKS
            await using var blockmarker = await database.CreateBlockMarkerAsync(result.TaskControl.ProgressToken).ConfigureAwait(false);
            var updateCount = 0L;
            await foreach (var restorelist in database.GetExistingFilesWithBlocks(result.TaskControl.ProgressToken).ConfigureAwait(false))
            {
                var rename = !options.Overwrite;
                var targetpath = restorelist.TargetPath;
                var targetfileid = restorelist.TargetFileID;
                var targetfilehash = restorelist.TargetHash;
                var targetfilelength = restorelist.Length;
                if (SystemIO.IO_OS.FileExists(targetpath))
                {
                    try
                    {
                        if (!await result.TaskControl.ProgressRendevouz().ConfigureAwait(false))
                            return;

                        var currentfilelength = SystemIO.IO_OS.FileLength(targetpath);
                        var wasTruncated = false;

                        // Adjust file length in overwrite mode if necessary (smaller is ok, will be extended during restore)
                        // We do it before scanning for blocks. This allows full verification on files that only needs to
                        // be truncated (i.e. forthwritten log files).
                        if (!rename && currentfilelength > targetfilelength)
                        {
                            var currentAttr = SystemIO.IO_OS.GetFileAttributes(targetpath);
                            if ((currentAttr & System.IO.FileAttributes.ReadOnly) != 0) // clear readonly attribute
                            {
                                if (options.Dryrun)
                                    Logging.Log.WriteDryrunMessage(LOGTAG, "WouldResetReadOnlyAttribute", "Would reset read-only attribute on file: {0}", targetpath);
                                else SystemIO.IO_OS.SetFileAttributes(targetpath, currentAttr & ~System.IO.FileAttributes.ReadOnly);
                            }
                            if (options.Dryrun)
                                Logging.Log.WriteDryrunMessage(LOGTAG, "WouldTruncateFile", "Would truncate file '{0}' to length of {1:N0} bytes", targetpath, targetfilelength);
                            else
                            {
                                using (var file = SystemIO.IO_OS.FileOpenWrite(targetpath))
                                    file.SetLength(targetfilelength);
                                currentfilelength = targetfilelength;
                            }
                            wasTruncated = true;
                        }

                        // If file size does not match and we have to rename on conflict,
                        // the whole scan can be skipped here because all blocks have to be restored anyway.
                        // For the other cases, we will check block and and file hashes and look for blocks
                        // to be restored and files that can already be verified.
                        if (!rename || currentfilelength == targetfilelength)
                        {
                            // a file hash for verification will only be necessary if the file has exactly
                            // the wanted size so we have a chance to already mark the file as data-verified.
                            bool calcFileHash = (currentfilelength == targetfilelength);
                            if (calcFileHash) filehasher.Initialize();

                            using (var file = SystemIO.IO_OS.FileOpenRead(targetpath))
                            using (var block = new Blockprocessor(file, blockbuffer))
                                await foreach (var targetblock in restorelist.Blocks(result.TaskControl.ProgressToken).ConfigureAwait(false))
                                {
                                    var size = block.Readblock();
                                    if (size <= 0)
                                        break;

                                    //TODO: Handle Metadata

                                    bool blockhashmatch = false;
                                    if (size == targetblock.Size)
                                    {
                                        // Parallelize file hash calculation on rename. Running read-only on same array should not cause conflicts or races.
                                        // Actually, in future always calculate the file hash and mark the file data as already verified.

                                        System.Threading.Tasks.Task calcFileHashTask = null;
                                        if (calcFileHash)
                                            calcFileHashTask = System.Threading.Tasks.Task.Run(
                                                () => filehasher.TransformBlock(blockbuffer, 0, size, blockbuffer, 0));

                                        var key = Convert.ToBase64String(blockhasher.ComputeHash(blockbuffer, 0, size));

                                        if (calcFileHashTask != null) await calcFileHashTask.ConfigureAwait(false); // wait because blockbuffer will be overwritten.

                                        if (key == targetblock.Hash)
                                        {
                                            await blockmarker
                                                .SetBlockRestored(targetfileid, targetblock.Index, key, size, false, result.TaskControl.ProgressToken)
                                                .ConfigureAwait(false);
                                            blockhashmatch = true;
                                        }
                                    }
                                    if (calcFileHash && !blockhashmatch) // will not be necessary anymore
                                    {
                                        filehasher.TransformFinalBlock(blockbuffer, 0, 0); // So a new initialize will not throw
                                        calcFileHash = false;
                                        if (rename) // file does not match. So break.
                                            break;
                                    }
                                }

                            bool fullfilehashmatch = false;
                            if (calcFileHash) // now check if files are identical
                            {
                                filehasher.TransformFinalBlock(blockbuffer, 0, 0);
                                var filekey = Convert.ToBase64String(filehasher.Hash);
                                fullfilehashmatch = (filekey == targetfilehash);
                            }

                            if (!rename && !fullfilehashmatch && !wasTruncated) // Reset read-only attribute (if set) to overwrite
                            {
                                var currentAttr = SystemIO.IO_OS.GetFileAttributes(targetpath);
                                if ((currentAttr & System.IO.FileAttributes.ReadOnly) != 0)
                                {
                                    if (options.Dryrun)
                                        Logging.Log.WriteDryrunMessage(LOGTAG, "WouldResetReadOnlyAttribyte", "Would reset read-only attribute on file: {0}", targetpath);
                                    else SystemIO.IO_OS.SetFileAttributes(targetpath, currentAttr & ~System.IO.FileAttributes.ReadOnly);
                                }
                            }

                            if (fullfilehashmatch)
                            {
                                //TODO: Check metadata to trigger rename? If metadata changed, it will still be restored for the file in-place.
                                await blockmarker
                                    .SetFileDataVerified(targetfileid, result.TaskControl.ProgressToken)
                                    .ConfigureAwait(false);
                                Logging.Log.WriteVerboseMessage(LOGTAG, "TargetExistsInCorrectVersion", "Target file exists{1} and is correct version: {0}", targetpath, wasTruncated ? " (but was truncated)" : "");
                                rename = false;
                            }
                            else if (rename)
                            {
                                // The new file will have none of the correct blocks,
                                // even if the scanned file had some
                                await blockmarker
                                    .SetAllBlocksMissing(targetfileid, result.TaskControl.ProgressToken)
                                    .ConfigureAwait(false);
                            }
                        }

                        if ((++updateCount) % 20 == 0)
                        {
                            await blockmarker
                                .UpdateProcessed(result.OperationProgressUpdater, result.TaskControl.ProgressToken)
                                .ConfigureAwait(false);
                            if (!await result.TaskControl.ProgressRendevouz().ConfigureAwait(false))
                                return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logging.Log.WriteWarningMessage(LOGTAG, "TargetFileReadError", ex, "Failed to read target file: \"{0}\", message: {1}", targetpath, ex.Message);
                        if (ex.IsAbortException())
                            throw;
                        if (options.UnittestMode)
                            throw;
                    }
                }
                else
                {
                    Logging.Log.WriteVerboseMessage(LOGTAG, "MissingTargetFile", "Target file does not exist: {0}", targetpath);
                    rename = false;
                }

                if (rename)
                {
                    //Select a new filename
                    var ext = SystemIO.IO_OS.PathGetExtension(targetpath) ?? "";
                    if (!string.IsNullOrEmpty(ext) && !ext.StartsWith(".", StringComparison.Ordinal))
                        ext = "." + ext;

                    // First we try with a simple date append, assuming that there are not many conflicts there
                    var newname = SystemIO.IO_OS.PathChangeExtension(targetpath, null) + "." + database.RestoreTime.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    var tr = newname + ext;
                    var c = 0;
                    while (SystemIO.IO_OS.FileExists(tr) && c < 1000)
                    {
                        try
                        {
                            // If we have a file with the correct name,
                            // it is most likely the file we want
                            filehasher.Initialize();

                            string key;
                            using (var file = SystemIO.IO_OS.FileOpenRead(tr))
                                key = Convert.ToBase64String(filehasher.ComputeHash(file));

                            if (key == targetfilehash)
                            {
                                //TODO: Also needs metadata check to make correct decision.
                                //      We stick to the policy to restore metadata in place, if data ok. So, metadata block may be restored.
                                await blockmarker
                                    .SetAllBlocksRestored(targetfileid, false, result.TaskControl.ProgressToken)
                                    .ConfigureAwait(false);
                                await blockmarker
                                    .SetFileDataVerified(targetfileid, result.TaskControl.ProgressToken)
                                    .ConfigureAwait(false);
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logging.Log.WriteWarningMessage(LOGTAG, "FailedToReadRestoreTarget", ex, "Failed to read candidate restore target {0}", tr);
                            if (options.UnittestMode)
                                throw;
                        }
                        tr = newname + " (" + (c++).ToString() + ")" + ext;
                    }

                    newname = tr;

                    Logging.Log.WriteVerboseMessage(LOGTAG, "TargetFileRetargeted", "Target file exists and will be restored to: {0}", newname);
                    await database
                        .UpdateTargetPath(targetfileid, newname, result.TaskControl.ProgressToken)
                        .ConfigureAwait(false);
                }

            }

            await blockmarker
                .UpdateProcessed(result.OperationProgressUpdater, result.TaskControl.ProgressToken)
                .ConfigureAwait(false);
            await blockmarker
                .CommitAsync(result.TaskControl.ProgressToken)
                .ConfigureAwait(false);
        }
    }
}
