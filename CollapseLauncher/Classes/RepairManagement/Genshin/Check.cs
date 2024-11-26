using CollapseLauncher.GameVersioning;
using CollapseLauncher.Helper;
using Hi3Helper;
using Hi3Helper.Data;
using Hi3Helper.EncTool.Parser.AssetIndex;
using Hi3Helper.SentryHelper;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using static Hi3Helper.Locale;
using static Hi3Helper.Logger;

namespace CollapseLauncher
{
    internal partial class GenshinRepair
    {
        private async Task Check(List<PkgVersionProperties> assetIndex, CancellationToken token)
        {
            List<PkgVersionProperties> brokenAssetIndex = new List<PkgVersionProperties>();

            // Set Indetermined status as false
            _status!.IsProgressAllIndetermined = false;
            _status.IsProgressPerFileIndetermined = false;

            // Show the asset entry panel
            _status.IsAssetEntryPanelShow = true;

            // Reset stopwatch
            RestartStopwatch();

            // Ensure to delete the DXSetup files
            EnsureDeleteDXSetupFiles();

            // Try move persistent files to StreamingAssets
            if (_isParsePersistentManifestSuccess) TryMovePersistentToStreamingAssets(assetIndex);

            // Check for any redundant files
            await CheckRedundantFiles(brokenAssetIndex, token);

            // Await the task for parallel processing
            try
            {
                // Await the task for parallel processing
                // and iterate assetIndex and check it using different method for each type and run it in parallel
                await Parallel.ForEachAsync(assetIndex, new ParallelOptions { MaxDegreeOfParallelism = _threadCount, CancellationToken = token }, async (asset, threadToken) =>
                {
                    await CheckAssetAllType(asset, brokenAssetIndex, threadToken);
                });
            }
            catch (AggregateException ex)
            {
                var innerExceptionsFirst = ex.Flatten().InnerExceptions.First();
                SentryHelper.ExceptionHandler(innerExceptionsFirst, SentryHelper.ExceptionType.UnhandledOther);
                throw innerExceptionsFirst;
            }

            // Re-add the asset index with a broken asset index
            assetIndex.Clear();
            assetIndex.AddRange(brokenAssetIndex);
        }

        private void EnsureDeleteDXSetupFiles()
        {
            // Check if the DXSETUP file is exist, then delete it.
            // The DXSETUP files causes some false positive detection of data modification
            // for some games (like Genshin, which causes 4302-x error for some reason)
            string dxSetupDir = Path.Combine(_gamePath, "DXSETUP");
            TryDeleteReadOnlyDir(dxSetupDir);
        }

        private void TryMovePersistentToStreamingAssets(IEnumerable<PkgVersionProperties> assetIndex)
        {
            if (!Directory.Exists(_gamePersistentPath)) return;
            TryMoveAudioPersistent(assetIndex);
            TryMoveVideoPersistent();
        }

        private void TryMoveAudioPersistent(IEnumerable<PkgVersionProperties> assetIndex)
        {
            // Try get the exclusion list of the audio (language specific) files
            string[] exclusionList = assetIndex
                .Where(x => x.isForceStoreInPersistent && x.remoteName
                    .AsSpan()
                    .EndsWith(".pck"))
                .Select(x => x.remoteNamePersistent
                    .Replace('/', '\\'))
                .ToArray();

            // Get the audio directory paths and create if doesn't exist
            string audioAsbPath = Path.Combine(_gameStreamingAssetsPath, "AudioAssets");
            string audioPersistentPath = Path.Combine(_gamePersistentPath, "AudioAssets");
            if (!Directory.Exists(audioPersistentPath)) return;
            if (!Directory.Exists(audioAsbPath)) Directory.CreateDirectory(audioAsbPath);

            // Get the list of audio language names from _gameVersionManager
            List<string> audioLangList = ((GameTypeGenshinVersion)_gameVersionManager)._audioVoiceLanguageList;

            // Enumerate the content of audio persistent directory
            foreach (string path in Directory.EnumerateDirectories(audioPersistentPath, "*", SearchOption.TopDirectoryOnly))
            {
                // Get the last path section as language name to compare
                string langName = Path.GetFileName(path);

                // If the path section matches the name in language list, then continue
                if (audioLangList.Contains(langName))
                {
                    // Enumerate the files that's being exist in the persistent path of each language
                    // except the one that's included in the exclusion list
                    foreach (string filePath in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                        .Where(x => !exclusionList.Any(y => x.AsSpan().EndsWith(y.AsSpan()))))
                    {
                        // Trim the path name to get the generic languageName/filename form
                        string pathName = filePath.AsSpan().Slice(audioPersistentPath.Length + 1).ToString();
                        // Combine the generic name with audioAsbPath
                        string newPath = EnsureCreationOfDirectory(Path.Combine(audioAsbPath, pathName));

                        // Try move the file to the asb path
                        File.Move(filePath, newPath, true);
                    }
                }
            }
        }

        private void TryMoveVideoPersistent()
        {
            string videoAsbPath = Path.Combine(_gameStreamingAssetsPath, "VideoAssets");
            string videoPersistentPath = Path.Combine(_gamePersistentPath, "VideoAssets");
            if (!Directory.Exists(videoPersistentPath)) return;
            if (!Directory.Exists(videoAsbPath)) Directory.CreateDirectory(videoAsbPath);
            MoveFolderContent(videoPersistentPath, videoAsbPath);
        }

        private async ValueTask CheckAssetAllType(PkgVersionProperties asset, List<PkgVersionProperties> targetAssetIndex, CancellationToken token)
        {
            // Update activity status
            _status!.ActivityStatus = string.Format(Lang._GameRepairPage.Status6, asset.remoteName);

            // Increment current total count
            _progressAllCountCurrent++;

            // Reset per file size counter
            _progressPerFileSizeTotal = asset.fileSize;
            _progressPerFileSizeCurrent = 0;

            // Get file path
            string filePath = Path.Combine(_gamePath, ConverterTool.NormalizePath(asset.remoteName));

            var forceStreamingAssets = false;
            if (asset.remoteName.Contains("StreamingAssets") && asset.isPatch)
            {
                asset.isForceStoreInPersistent = false;
                asset.isForceStoreInStreaming  = true;
                forceStreamingAssets           = true;
            }
            // Get persistent and streaming paths
            FileInfo fileInfoPersistent = asset.remoteNamePersistent == null ?
                null :
                new FileInfo(Path.Combine(_gamePath, ConverterTool.NormalizePath(asset.remoteNamePersistent)))
                .EnsureNoReadOnly();
            FileInfo fileInfoStreaming = new FileInfo(filePath).EnsureNoReadOnly();

            bool UsePersistent = (asset.isForceStoreInPersistent && !asset.isForceStoreInStreaming && fileInfoPersistent != null && !fileInfoPersistent.Exists) ||
                                 (asset.isPatch && !forceStreamingAssets) || (!fileInfoStreaming.Exists && !asset.isForceStoreInStreaming);
            bool IsPersistentExist = fileInfoPersistent != null && fileInfoPersistent.Exists && fileInfoPersistent.Length == asset.fileSize;
            bool IsStreamingExist = fileInfoStreaming.Exists && fileInfoStreaming.Length == asset.fileSize;
           
            // Update the local path to full persistent or streaming path and add asset for missing/unmatched size file
            if (asset.remoteNamePersistent != null)
                asset.remoteName = UsePersistent ? asset.remoteNamePersistent : asset.remoteName;

            // Check if the file exist on both persistent and streaming path, then mark the
            // streaming path as redundant (unused)
            if (IsPersistentExist && IsStreamingExist && !asset.isPatch)
            {
                // Add the count and asset. Mark the type as "RepairAssetType.Unused"
                _progressAllCountFound++;

                Dispatch(() => AssetEntry.Add(
                    new AssetProperty<RepairAssetType>(
                        Path.GetFileName(fileInfoStreaming.FullName),
                        RepairAssetType.Unused,
                        Path.GetDirectoryName(fileInfoStreaming.FullName),
                        asset.fileSize,
                        null,
                        null
                    )
                ));

                asset.type = "Unused";
                asset.localName = fileInfoStreaming.FullName;
                targetAssetIndex.Add(asset);

                LogWriteLine($"File [T: {asset.type}]: {fileInfoStreaming.FullName} is redundant (exist both in persistent and streaming)", LogType.Warning, true);
            }

            // Prevent assigning remoteNamePersistent when its null
            string repairFile = !string.IsNullOrEmpty(asset.remoteNamePersistent) 
                && UsePersistent ? asset.remoteNamePersistent : asset.remoteName;

            // Check if the persistent or streaming file doesn't exist
            if ((UsePersistent && !IsPersistentExist) || (!IsStreamingExist && !IsPersistentExist))
            {
                AddNotFoundOrMismatchAsset(asset);
            }
            
            // Iterate between Persistent and StreamingAssets folder if it sees the file is not exist
            var file = UsePersistent ? fileInfoPersistent : fileInfoStreaming;
            if (!file!.Exists)
            {
                file = !UsePersistent ? fileInfoPersistent! : fileInfoStreaming;
                if (!file.Exists) AddNotFoundOrMismatchAsset(asset);
                
                var origFilePath = UsePersistent ? fileInfoPersistent.FullName : fileInfoStreaming.FullName;
                file.MoveTo(origFilePath); // Move the file to the correct path
                LogWriteLine($"[CheckAssetAllType] Moving files to their correct path\r\n\t" +
                             $"Current path: {file.FullName}\r\n\t" +
                             $"Target path :  {origFilePath}", LogType.Default, true);
                file = new FileInfo(origFilePath).EnsureNoReadOnly();
            }

            // Skip CRC check if fast method is used
            if (_useFastMethod)
            {
                return;
            }
            
            try
            {
                // Open and read fileInfo as FileStream 
                await using FileStream filefs =
                    await NaivelyOpenFileStreamAsync(file,
                                                     FileMode.Open,
                                                     FileAccess.Read,
                                                     FileShare.Read);
                // If pass the check above, then do CRC calculation
                // Additional: the total file size progress is disabled and will be incremented after this
                byte[] localCRC = await CheckHashAsync(filefs, MD5.Create(), token);

                // If local and asset CRC doesn't match, then add the asset
                byte[] remoteCRC = HexTool.HexToBytesUnsafe(asset.md5);
                if (!IsArrayMatch(localCRC, remoteCRC))
                {
                    // Update the total progress and found counter
                    _progressAllSizeFound += asset.fileSize;
                    _progressAllCountFound++;

                    // Set the per size progress
                    _progressPerFileSizeCurrent = asset.fileSize;

                    // Increment the total current progress
                    _progressAllSizeCurrent += asset.fileSize;

                    Dispatch(() => AssetEntry.Add(
                                                  new AssetProperty<RepairAssetType>(
                                                       Path.GetFileName(asset.remoteName),
                                                       RepairAssetType.Generic,
                                                       Path.GetDirectoryName(asset.remoteName),
                                                       asset.fileSize,
                                                       localCRC,
                                                       remoteCRC
                                                      )
                                                 ));
                    targetAssetIndex.Add(asset);

                    LogWriteLine($"File [T: {asset.type}]: {filefs.Name} is broken! Index CRC: {asset.md5} <--> File CRC: {HexTool.BytesToHexUnsafe(localCRC)}",
                                 LogType.Warning, true);
                }
            }
            catch (FileNotFoundException) // If file for ANY reason does not exist after the first check, try to
            {
                try
                {
                    TryUnassignReadOnlyFileSingle(file.FullName);
                    file.Delete();
                }
                catch (Exception e)
                {
                    LogWriteLine($"[CheckAssetAllType] Error while trying to delete file: {file.FullName}\n{e}",
                                 LogType.Error, true);
                    ErrorSender.SendException(e);
                }
                
                
                Dispatch(() => AssetEntry.Add(
                                              new AssetProperty<RepairAssetType>(
                                                   Path.GetFileName(file.FullName),
                                                   RepairAssetType.Generic,
                                                   Path.GetDirectoryName(file.FullName),
                                                   asset.fileSize,
                                                   null,
                                                   HexTool.HexToBytesUnsafe(asset.md5)
                                                  )
                                             ));
                targetAssetIndex.Add(asset);
                LogWriteLine($"File [T: {asset.type}]: {repairFile} is not found or has unmatched size", LogType.Warning, true);
            }

            return;

            void AddNotFoundOrMismatchAsset(PkgVersionProperties assetInner)
            {
                // Update the total progress and found counter
                _progressAllSizeFound += assetInner.fileSize;
                _progressAllCountFound++;

                // Set the per size progress
                _progressPerFileSizeCurrent = assetInner.fileSize;

                // Increment the total current progress
                _progressAllSizeCurrent += assetInner.fileSize;

                Dispatch(() => AssetEntry.Add(
                                              new AssetProperty<RepairAssetType>(
                                                   Path.GetFileName(repairFile),
                                                   RepairAssetType.Generic,
                                                   Path.GetDirectoryName(repairFile),
                                                   assetInner.fileSize,
                                                   null,
                                                   HexTool.HexToBytesUnsafe(assetInner.md5)
                                                  )
                                             ));
                targetAssetIndex.Add(assetInner);

                LogWriteLine($"File [T: {RepairAssetType.Generic}]: {(string.IsNullOrEmpty(repairFile) ? assetInner.localName : repairFile)} is not found or has unmatched size",
                             LogType.Warning, true);
            }
        }

        #region UnusedFiles
        private async Task CheckRedundantFiles(List<PkgVersionProperties> targetAssetIndex, CancellationToken token)
        {
            // Initialize FilePath and FileInfo
            string FilePath;
            FileInfo fInfo;

            // Iterate the available deletefiles files
            DirectoryInfo directoryInfo = new DirectoryInfo(_gamePath);
            foreach (FileInfo listFile in directoryInfo
                .EnumerateFiles("*deletefiles*", SearchOption.TopDirectoryOnly)
                .EnumerateNoReadOnly())
            {
                LogWriteLine($"deletefiles file list path: {listFile}", LogType.Default, true);

                // Use deletefiles files to get the list of the redundant file
                await using Stream fs         = await listFile.NaivelyOpenFileStreamAsync(FileMode.Open, FileAccess.Read, FileShare.None, FileOptions.DeleteOnClose);
                using StreamReader listReader = new StreamReader(fs);
                while (!listReader.EndOfStream)
                {
                    // Get the File name and FileInfo
                    FilePath = Path.Combine(_gamePath, ConverterTool.NormalizePath(await listReader.ReadLineAsync(token)));
                    fInfo    = new FileInfo(FilePath).EnsureNoReadOnly();

                    // If the file doesn't exist, then continue
                    if (!fInfo.Exists)
                        continue;

                    // Update total found progress
                    _progressAllCountFound++;

                    // Get the stripped relative name
                    string strippedName = fInfo.FullName.AsSpan()[(_gamePath.Length + 1)..].ToString();

                    // Assign the asset before adding to targetAssetIndex
                    PkgVersionProperties asset = new PkgVersionProperties
                    {
                        localName = strippedName,
                        fileSize  = fInfo.Length,
                        type      = RepairAssetType.Unused.ToString()
                    };
                    Dispatch(() => AssetEntry.Add(
                                                  new AssetProperty<RepairAssetType>(
                                                       Path.GetFileName(asset.localName),
                                                       RepairAssetType.Unused,
                                                       Path.GetDirectoryName(asset.localName),
                                                       asset.fileSize,
                                                       null,
                                                       null
                                                      )
                                                 ));

                    // Add the asset into targetAssetIndex
                    targetAssetIndex.Add(asset);
                    LogWriteLine($"Redundant file has been found: {strippedName}", LogType.Default, true);
                }
            }

            // Iterate redundant diff and temporary files
            foreach (FileInfo fileInfo in directoryInfo.EnumerateFiles("*.*", SearchOption.AllDirectories)
                .EnumerateNoReadOnly()
                .Where(x => x.Name.EndsWith(".diff",  StringComparison.OrdinalIgnoreCase)
                                 || x.Name.EndsWith("_tmp",   StringComparison.OrdinalIgnoreCase)
                                 || x.Name.EndsWith(".hdiff", StringComparison.OrdinalIgnoreCase)))
            {
                // Update total found progress
                _progressAllCountFound++;

                // Get the stripped relative name
                string strippedName = fileInfo.FullName.AsSpan()[(_gamePath.Length + 1)..].ToString();

                // Assign the asset before adding to targetAssetIndex
                PkgVersionProperties asset = new PkgVersionProperties
                {
                    localName = strippedName,
                    fileSize  = fileInfo.Length,
                    type      = RepairAssetType.Unused.ToString()
                };
                Dispatch(() => AssetEntry.Add(
                    new AssetProperty<RepairAssetType>(
                        Path.GetFileName(asset.localName),
                        RepairAssetType.Unused,
                        Path.GetDirectoryName(asset.localName),
                        asset.fileSize,
                        null,
                        null
                    )
                ));

                // Add the asset into targetAssetIndex
                targetAssetIndex.Add(asset);
                LogWriteLine($"Redundant file has been found: {strippedName}", LogType.Default, true);
            }
        }
        #endregion
    }
}
