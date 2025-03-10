﻿using Microsoft.Win32.SafeHandles;
using RX_Explorer.Dialog;
using RX_Explorer.Interface;
using ShareClassLibrary;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.UI.Core;
using Windows.UI.Xaml.Media.Imaging;

namespace RX_Explorer.Class
{
    /// <summary>
    /// 提供对设备中的存储对象的描述
    /// </summary>
    public abstract class FileSystemStorageItemBase : IStorageItemPropertiesBase, INotifyPropertyChanged, IStorageItemOperation, IEquatable<FileSystemStorageItemBase>
    {
        public string Path { get; protected set; }

        public virtual string SizeDescription { get; }

        public virtual string Name => System.IO.Path.GetFileName(Path) ?? string.Empty;

        public virtual string DisplayName => Name;

        public virtual string Type => System.IO.Path.GetExtension(Path)?.ToUpper() ?? string.Empty;

        public virtual string DisplayType => Type;

        public ColorTag ColorTag
        {
            get
            {
                return SQLite.Current.GetColorTag(Path);
            }
            set
            {
                SQLite.Current.SetColorTag(Path, value);
                OnPropertyChanged();
            }
        }

        private bool ThubmnalModeChanged;

        public double ThumbnailOpacity { get; protected set; } = 1d;

        public ulong Size { get; protected set; }

        public DateTimeOffset CreationTime { get; protected set; }

        public DateTimeOffset ModifiedTime { get; protected set; }

        public virtual string ModifiedTimeDescription
        {
            get
            {
                if (ModifiedTime != DateTimeOffset.MaxValue.ToLocalTime() && ModifiedTime != DateTimeOffset.MinValue.ToLocalTime())
                {
                    return ModifiedTime.ToString("G");
                }
                else
                {
                    return string.Empty;
                }
            }
        }

        public virtual string CreationTimeDescription
        {
            get
            {
                if (CreationTime == DateTimeOffset.MaxValue.ToLocalTime())
                {
                    return Globalization.GetString("UnknownText");
                }
                else
                {
                    return CreationTime.ToString("G");
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public virtual BitmapImage Thumbnail { get; private set; }

        public virtual BitmapImage ThumbnailOverlay { get; protected set; }

        public virtual bool IsReadOnly { get; protected set; }

        public virtual bool IsSystemItem { get; protected set; }

        protected virtual bool ShouldGenerateThumbnail => (this is FileSystemStorageFile && SettingPage.ContentLoadMode == LoadMode.OnlyFile) || SettingPage.ContentLoadMode == LoadMode.All;

        protected ThumbnailMode ThumbnailMode { get; set; } = ThumbnailMode.ListView;

        public SyncStatus SyncStatus { get; protected set; } = SyncStatus.Unknown;

        private int IsLoaded;
        protected IStorageItem StorageItem { get; set; }

        protected static readonly Uri Const_Folder_Image_Uri = WindowsVersionChecker.IsNewerOrEqual(Version.Windows11)
                                                                 ? new Uri("ms-appx:///Assets/FolderIcon_Win11.png")
                                                                 : new Uri("ms-appx:///Assets/FolderIcon_Win10.png");

        protected static readonly Uri Const_File_White_Image_Uri = new Uri("ms-appx:///Assets/Page_Solid_White.png");

        protected static readonly Uri Const_File_Black_Image_Uri = new Uri("ms-appx:///Assets/Page_Solid_Black.png");

        public static async Task<bool> CheckExistAsync(string Path)
        {
            if (!string.IsNullOrEmpty(Path) && System.IO.Path.IsPathRooted(Path))
            {
                try
                {
                    try
                    {
                        return await Task.Run(() => Win32_Native_API.CheckExist(Path));
                    }
                    catch (LocationNotAvailableException)
                    {
                        string DirectoryPath = System.IO.Path.GetDirectoryName(Path);

                        if (string.IsNullOrEmpty(DirectoryPath))
                        {
                            await StorageFolder.GetFolderFromPathAsync(Path);
                            return true;
                        }
                        else
                        {
                            StorageFolder Folder = await StorageFolder.GetFolderFromPathAsync(DirectoryPath);

                            if (await Folder.TryGetItemAsync(System.IO.Path.GetFileName(Path)) != null)
                            {
                                return true;
                            }
                            else
                            {
                                return false;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogTracer.Log(ex, "CheckExist threw an exception");
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        public static Task<IReadOnlyList<FileSystemStorageItemBase>> OpenInBatchAsync(IEnumerable<string> PathArray)
        {
            return Task.Factory.StartNew<IReadOnlyList<FileSystemStorageItemBase>>(() =>
            {
                ConcurrentBag<FileSystemStorageItemBase> Result = new ConcurrentBag<FileSystemStorageItemBase>();
                ConcurrentBag<(string, Exception)> RetryBag = new ConcurrentBag<(string, Exception)>();

                Parallel.ForEach(PathArray, (Path) =>
                {
                    try
                    {
                        if (Win32_Native_API.GetStorageItem(Path) is FileSystemStorageItemBase Item)
                        {
                            Result.Add(Item);
                        }
                    }
                    catch (LocationNotAvailableException)
                    {
                        try
                        {
                            string DirectoryPath = System.IO.Path.GetDirectoryName(Path);

                            if (string.IsNullOrEmpty(DirectoryPath))
                            {
                                StorageFolder Folder = StorageFolder.GetFolderFromPathAsync(Path).AsTask().Result;
                                Result.Add(new FileSystemStorageFolder(Folder));
                            }
                            else
                            {
                                StorageFolder ParentFolder = StorageFolder.GetFolderFromPathAsync(DirectoryPath).AsTask().Result;

                                switch (ParentFolder.TryGetItemAsync(System.IO.Path.GetFileName(Path)).AsTask().Result)
                                {
                                    case StorageFolder Folder:
                                        {
                                            Result.Add(new FileSystemStorageFolder(Folder));
                                            break;
                                        }
                                    case StorageFile File:
                                        {
                                            Result.Add(new FileSystemStorageFile(File));
                                            break;
                                        }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (ex is not FileNotFoundException or DirectoryNotFoundException)
                            {
                                RetryBag.Add((Path, ex));
                            }
                        }
                    }
                });

                using (FullTrustProcessController.ExclusiveUsage Exclusive = FullTrustProcessController.GetAvailableController().Result)
                {
                    foreach ((string Path, Exception ex) in RetryBag)
                    {
                        using (SafeFileHandle Handle = Exclusive.Controller.GetFileHandleAsync(Path, AccessMode.ReadWrite).Result)
                        {
                            if (Handle.IsInvalid)
                            {
                                LogTracer.Log(ex, $"{nameof(OpenInBatchAsync)} failed and could not get the storage item, path:\"{Path}\"");
                            }
                            else
                            {
                                LogTracer.Log($"Try get storage item from {nameof(Win32_Native_API.GetStorageItemFromHandle)}");

                                if (Win32_Native_API.GetStorageItemFromHandle(Path, Handle.DangerousGetHandle()) is FileSystemStorageItemBase Item)
                                {
                                    Result.Add(Item);
                                }
                                else
                                {
                                    LogTracer.Log(ex, $"{nameof(OpenInBatchAsync)} failed and could not get the storage item, path:\"{Path}\"");
                                }
                            }
                        }
                    }
                }

                return Result.ToList();
            }, TaskCreationOptions.LongRunning);
        }

        public static async Task<FileSystemStorageItemBase> OpenAsync(string Path)
        {
            try
            {
                try
                {
                    return await Task.Run(() => Win32_Native_API.GetStorageItem(Path));
                }
                catch (LocationNotAvailableException)
                {
                    try
                    {
                        string DirectoryPath = System.IO.Path.GetDirectoryName(Path);

                        if (string.IsNullOrEmpty(DirectoryPath))
                        {
                            StorageFolder Folder = await StorageFolder.GetFolderFromPathAsync(Path);
                            return new FileSystemStorageFolder(Folder);
                        }
                        else
                        {
                            StorageFolder ParentFolder = await StorageFolder.GetFolderFromPathAsync(DirectoryPath);

                            switch (await ParentFolder.TryGetItemAsync(System.IO.Path.GetFileName(Path)))
                            {
                                case StorageFolder Folder:
                                    {
                                        return new FileSystemStorageFolder(Folder);
                                    }
                                case StorageFile File:
                                    {
                                        return new FileSystemStorageFile(File);
                                    }
                                default:
                                    {
                                        LogTracer.Log($"UWP storage API could not found the path: \"{Path}\"");
                                        return null;
                                    }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not FileNotFoundException or DirectoryNotFoundException)
                    {
                        using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                        using (SafeFileHandle Handle = await Exclusive.Controller.GetFileHandleAsync(Path, AccessMode.ReadWrite))
                        {
                            if (Handle.IsInvalid)
                            {
                                throw;
                            }
                            else
                            {
                                LogTracer.Log($"Try get storageitem from {nameof(Win32_Native_API.GetStorageItemFromHandle)}");
                                return Win32_Native_API.GetStorageItemFromHandle(Path, Handle.DangerousGetHandle());
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex, $"{nameof(OpenAsync)} failed and could not get the storage item, path:\"{Path}\"");
                return null;
            }
        }

        public static async Task<FileSystemStorageItemBase> CreateNewAsync(string Path, StorageItemTypes ItemTypes, CreateOption Option)
        {
            switch (ItemTypes)
            {
                case StorageItemTypes.File:
                    {
                        try
                        {
                            if (Win32_Native_API.CreateFileFromPath(Path, Option, out string NewPath))
                            {
                                return await OpenAsync(NewPath);
                            }
                            else
                            {
                                StorageFolder Folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(Path));

                                switch (Option)
                                {
                                    case CreateOption.GenerateUniqueName:
                                        {
                                            StorageFile NewFile = await Folder.CreateFileAsync(System.IO.Path.GetFileName(Path), CreationCollisionOption.GenerateUniqueName);
                                            return new FileSystemStorageFile(NewFile);
                                        }
                                    case CreateOption.OpenIfExist:
                                        {
                                            StorageFile NewFile = await Folder.CreateFileAsync(System.IO.Path.GetFileName(Path), CreationCollisionOption.OpenIfExists);
                                            return new FileSystemStorageFile(NewFile);
                                        }
                                    case CreateOption.ReplaceExisting:
                                        {
                                            StorageFile NewFile = await Folder.CreateFileAsync(System.IO.Path.GetFileName(Path), CreationCollisionOption.ReplaceExisting);
                                            return new FileSystemStorageFile(NewFile);
                                        }
                                    default:
                                        {
                                            return null;
                                        }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogTracer.Log(ex, $"{nameof(CreateNewAsync)} failed and could not create the storage item, path:\"{Path}\"");

                            using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                            {
                                string NewItemPath = await Exclusive.Controller.CreateNewAsync(CreateType.File, Path);

                                if (string.IsNullOrEmpty(NewItemPath))
                                {
                                    LogTracer.Log("Elevated FullTrustProcess could not create a new file");
                                    return null;
                                }
                                else
                                {
                                    return await OpenAsync(NewItemPath);
                                }
                            }
                        }
                    }
                case StorageItemTypes.Folder:
                    {
                        try
                        {
                            if (Win32_Native_API.CreateDirectoryFromPath(Path, Option, out string NewPath))
                            {
                                return await OpenAsync(NewPath);
                            }
                            else
                            {
                                StorageFolder Folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(Path));

                                switch (Option)
                                {
                                    case CreateOption.GenerateUniqueName:
                                        {
                                            StorageFolder NewFolder = await Folder.CreateFolderAsync(System.IO.Path.GetFileName(Path), CreationCollisionOption.GenerateUniqueName);
                                            return new FileSystemStorageFolder(NewFolder);
                                        }
                                    case CreateOption.OpenIfExist:
                                        {
                                            StorageFolder NewFolder = await Folder.CreateFolderAsync(System.IO.Path.GetFileName(Path), CreationCollisionOption.OpenIfExists);
                                            return new FileSystemStorageFolder(NewFolder);
                                        }
                                    case CreateOption.ReplaceExisting:
                                        {
                                            StorageFolder NewFolder = await Folder.CreateFolderAsync(System.IO.Path.GetFileName(Path), CreationCollisionOption.ReplaceExisting);
                                            return new FileSystemStorageFolder(NewFolder);
                                        }
                                    default:
                                        {
                                            return null;
                                        }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogTracer.Log(ex, $"{nameof(CreateNewAsync)} failed and could not create the storage item, path:\"{Path}\"");

                            using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                            {
                                string NewItemPath = await Exclusive.Controller.CreateNewAsync(CreateType.Folder, Path);

                                if (string.IsNullOrEmpty(NewItemPath))
                                {
                                    LogTracer.Log("Elevated FullTrustProcess could not create new");
                                    return null;
                                }
                                else
                                {
                                    return await OpenAsync(NewItemPath);
                                }
                            }
                        }
                    }
                default:
                    {
                        return null;
                    }
            }
        }

        protected FileSystemStorageItemBase(string Path, SafeFileHandle Handle, bool LeaveOpen) : this(Win32_Native_API.GetStorageItemRawDataFromHandle(Path, Handle.DangerousGetHandle()))
        {
            if (!LeaveOpen)
            {
                Handle.Dispose();
            }
        }

        protected FileSystemStorageItemBase(Win32_File_Data Data)
        {
            Path = Data.Path;

            if (Data.IsDataValid)
            {
                IsReadOnly = Data.IsReadOnly;
                IsSystemItem = Data.IsSystemItem;
                Size = Data.Size;
                ModifiedTime = Data.ModifiedTime;
                CreationTime = Data.CreationTime;
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string PropertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(PropertyName));
        }

        public virtual void SetThumbnailOpacity(ThumbnailStatus Status)
        {
            switch (Status)
            {
                case ThumbnailStatus.Normal:
                    {
                        if (ThumbnailOpacity != 1d)
                        {
                            ThumbnailOpacity = 1d;
                        }

                        break;
                    }
                case ThumbnailStatus.ReducedOpacity:
                    {
                        if (ThumbnailOpacity != 0.5)
                        {
                            ThumbnailOpacity = 0.5;
                        }

                        break;
                    }
            }

            OnPropertyChanged(nameof(ThumbnailOpacity));
        }


        public void SetThumbnailMode(ThumbnailMode Mode)
        {
            if (Mode != ThumbnailMode)
            {
                ThumbnailMode = Mode;
                ThubmnalModeChanged = true;
            }
        }

        public async Task LoadAsync()
        {
            if (Interlocked.Exchange(ref IsLoaded, 1) > 0)
            {
                if (ThubmnalModeChanged)
                {
                    ThubmnalModeChanged = false;

                    if (ShouldGenerateThumbnail)
                    {
                        try
                        {
                            using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                            {
                                Thumbnail = await GetThumbnailAsync(Exclusive.Controller, ThumbnailMode);
                            }
                        }
                        catch (Exception ex)
                        {
                            LogTracer.Log(ex, $"An exception was threw in {nameof(LoadAsync)}, StorageType: {GetType().FullName}, Path: {Path}");
                        }
                        finally
                        {
                            OnPropertyChanged(nameof(Thumbnail));
                        }
                    }
                }
            }
            else
            {
                async void LocalLoadFunction()
                {
                    try
                    {
                        using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                        {
                            if (ShouldGenerateThumbnail)
                            {
                                await LoadCoreAsync(Exclusive.Controller, false);

                                if (await GetThumbnailAsync(Exclusive.Controller, ThumbnailMode) is BitmapImage Thumbnail)
                                {
                                    this.Thumbnail = Thumbnail;
                                }
                            }

                            ThumbnailOverlay = await GetThumbnailOverlayAsync(Exclusive.Controller);
                        }

                        if (SpecialPath.IsPathIncluded(Path, SpecialPath.SpecialPathEnum.OneDrive))
                        {
                            await GetSyncStatusAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        LogTracer.Log(ex, $"An exception was threw in {nameof(LocalLoadFunction)}, StorageType: {GetType().FullName}, Path: {Path}");
                    }
                    finally
                    {
                        OnPropertyChanged(nameof(Name));
                        OnPropertyChanged(nameof(SizeDescription));
                        OnPropertyChanged(nameof(DisplayType));
                        OnPropertyChanged(nameof(ModifiedTimeDescription));
                        OnPropertyChanged(nameof(Thumbnail));
                        OnPropertyChanged(nameof(ThumbnailOverlay));
                    }
                };

                if (CoreApplication.MainView.CoreWindow.Dispatcher.HasThreadAccess)
                {
                    LocalLoadFunction();
                }
                else
                {
                    await CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Low, LocalLoadFunction);
                }
            }
        }

        private async Task GetSyncStatusAsync()
        {
            switch (await GetStorageItemAsync())
            {
                case StorageFile File:
                    {
                        IDictionary<string, object> Properties = await File.Properties.RetrievePropertiesAsync(new string[] { "System.FilePlaceholderStatus", "System.FileOfflineAvailabilityStatus" });

                        if (!Properties.TryGetValue("System.FilePlaceholderStatus", out object StatusIndex))
                        {
                            if (!Properties.TryGetValue("System.FileOfflineAvailabilityStatus", out StatusIndex))
                            {
                                SyncStatus = SyncStatus.Unknown;
                                break;
                            }
                        }

                        switch (Convert.ToUInt32(StatusIndex))
                        {
                            case 0:
                            case 1:
                            case 8:
                                {
                                    SyncStatus = SyncStatus.AvailableOnline;
                                    break;
                                }
                            case 2:
                            case 3:
                            case 14:
                            case 15:
                                {
                                    SyncStatus = SyncStatus.AvailableOffline;
                                    break;
                                }
                            case 9:
                                {
                                    SyncStatus = SyncStatus.Sync;
                                    break;
                                }
                            case 4:
                                {
                                    SyncStatus = SyncStatus.Excluded;
                                    break;
                                }
                            default:
                                {
                                    SyncStatus = SyncStatus.Unknown;
                                    break;
                                }
                        }

                        break;
                    }
                case StorageFolder Folder:
                    {
                        IDictionary<string, object> Properties = await Folder.Properties.RetrievePropertiesAsync(new string[] { "System.FilePlaceholderStatus", "System.FileOfflineAvailabilityStatus" });


                        if (!Properties.TryGetValue("System.FileOfflineAvailabilityStatus", out object StatusIndex))
                        {
                            if (!Properties.TryGetValue("System.FilePlaceholderStatus", out StatusIndex))
                            {
                                SyncStatus = SyncStatus.Unknown;
                                break;
                            }
                        }

                        switch (Convert.ToUInt32(StatusIndex))
                        {
                            case 0:
                            case 1:
                            case 8:
                                {
                                    SyncStatus = SyncStatus.AvailableOnline;
                                    break;
                                }
                            case 2:
                            case 3:
                            case 14:
                            case 15:
                                {
                                    SyncStatus = SyncStatus.AvailableOffline;
                                    break;
                                }
                            case 9:
                                {
                                    SyncStatus = SyncStatus.Sync;
                                    break;
                                }
                            case 4:
                                {
                                    SyncStatus = SyncStatus.Excluded;
                                    break;
                                }
                            default:
                                {
                                    SyncStatus = SyncStatus.Unknown;
                                    break;
                                }
                        }

                        break;
                    }
                default:
                    {
                        SyncStatus = SyncStatus.Unknown;
                        break;
                    }
            }

            OnPropertyChanged(nameof(SyncStatus));
        }

        public async Task<SafeFileHandle> GetNativeHandleAsync(AccessMode Mode)
        {
            if (await GetStorageItemAsync() is IStorageItem Item)
            {
                return Item.GetSafeFileHandle(Mode);
            }
            else
            {
                using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                {
                    return await Exclusive.Controller.GetFileHandleAsync(Path, Mode);
                }
            }
        }

        protected virtual async Task<BitmapImage> GetThumbnailOverlayAsync(FullTrustProcessController Controller)
        {
            byte[] ThumbnailOverlayByteArray = await Controller.GetThumbnailOverlayAsync(Path);

            if (ThumbnailOverlayByteArray.Length > 0)
            {
                using (MemoryStream Ms = new MemoryStream(ThumbnailOverlayByteArray))
                {
                    BitmapImage Overlay = new BitmapImage();
                    await Overlay.SetSourceAsync(Ms.AsRandomAccessStream());
                    return Overlay;
                }
            }
            else
            {
                return null;
            }
        }

        protected abstract Task LoadCoreAsync(FullTrustProcessController Controller, bool ForceUpdate);

        public abstract Task<IStorageItem> GetStorageItemAsync();

        protected virtual async Task<BitmapImage> GetThumbnailAsync(FullTrustProcessController Controller, ThumbnailMode Mode)
        {
            async Task<BitmapImage> GetThumbnailTask()
            {
                byte[] ThumbnailData = await Controller.GetThumbnailAsync(Path);

                if (ThumbnailData.Length > 0)
                {
                    using (MemoryStream IconStream = new MemoryStream(ThumbnailData))
                    {
                        BitmapImage Image = new BitmapImage();
                        await Image.SetSourceAsync(IconStream.AsRandomAccessStream());
                        return Image;
                    }
                }
                else
                {
                    return null;
                }
            }

            if (await GetStorageItemAsync() is IStorageItem Item)
            {
                BitmapImage LocalThumbnail = await Item.GetThumbnailBitmapAsync(Mode);

                if (LocalThumbnail == null)
                {
                    return await GetThumbnailTask();
                }
                else
                {
                    return LocalThumbnail;
                }
            }
            else
            {
                return await GetThumbnailTask();
            }
        }

        public async Task RefreshAsync()
        {
            try
            {
                if (await CheckExistAsync(Path))
                {
                    using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                    {
                        await LoadCoreAsync(Exclusive.Controller, true);
                    }

                    OnPropertyChanged(nameof(SizeDescription));
                    OnPropertyChanged(nameof(Name));
                    OnPropertyChanged(nameof(ModifiedTimeDescription));
                    OnPropertyChanged(nameof(Thumbnail));
                    OnPropertyChanged(nameof(DisplayType));
                }
                else
                {
                    LogTracer.Log($"File/Folder not found or access deny when executing FileSystemStorageItemBase.Update, path: {Path}");
                }
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex, $"An exception was threw when executing FileSystemStorageItemBase.Update, path: {Path}");
            }
        }

        public virtual async Task MoveAsync(string DirectoryPath, CollisionOptions Option = CollisionOptions.None, ProgressChangedEventHandler ProgressHandler = null)
        {
            using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
            {
                await Exclusive.Controller.MoveAsync(Path, DirectoryPath, Option, true, ProgressHandler: ProgressHandler);
            }
        }

        public virtual Task MoveAsync(FileSystemStorageFolder Directory, CollisionOptions Option = CollisionOptions.None, ProgressChangedEventHandler ProgressHandler = null)
        {
            return MoveAsync(Directory.Path, Option, ProgressHandler);
        }

        public virtual async Task CopyAsync(string DirectoryPath, CollisionOptions Option = CollisionOptions.None, ProgressChangedEventHandler ProgressHandler = null)
        {
            using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
            {
                await Exclusive.Controller.CopyAsync(Path, DirectoryPath, Option, true, ProgressHandler: ProgressHandler);
            }
        }

        public virtual Task CopyAsync(FileSystemStorageFolder Directory, CollisionOptions Option = CollisionOptions.None, ProgressChangedEventHandler ProgressHandler = null)
        {
            return CopyAsync(Directory.Path, Option, ProgressHandler);
        }

        public async virtual Task<string> RenameAsync(string DesireName)
        {
            using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
            {
                string NewName = await Exclusive.Controller.RenameAsync(Path, DesireName);
                Path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path), NewName);
                return NewName;
            }
        }

        public virtual async Task DeleteAsync(bool PermanentDelete, ProgressChangedEventHandler ProgressHandler = null)
        {
            using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
            {
                await Exclusive.Controller.DeleteAsync(Path, PermanentDelete, true, ProgressHandler: ProgressHandler);
            }
        }

        public override string ToString()
        {
            return Name;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(this, obj))
            {
                return true;
            }
            else
            {
                if (obj is FileSystemStorageItemBase Item)
                {
                    return Item.Path.Equals(Path, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    return false;
                }
            }
        }

        public override int GetHashCode()
        {
            return Path.GetHashCode();
        }

        public bool Equals(FileSystemStorageItemBase other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }
            else
            {
                if (other == null)
                {
                    return false;
                }
                else
                {
                    return other.Path.Equals(Path, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        public static bool operator ==(FileSystemStorageItemBase left, FileSystemStorageItemBase right)
        {
            if (left is null)
            {
                return right is null;
            }
            else
            {
                if (right is null)
                {
                    return false;
                }
                else
                {
                    return left.Path.Equals(right.Path, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        public static bool operator !=(FileSystemStorageItemBase left, FileSystemStorageItemBase right)
        {
            if (left is null)
            {
                return right is object;
            }
            else
            {
                if (right is null)
                {
                    return true;
                }
                else
                {
                    return !left.Path.Equals(right.Path, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        public static explicit operator StorageFile(FileSystemStorageItemBase File)
        {
            return File.StorageItem as StorageFile;
        }

        public static explicit operator StorageFolder(FileSystemStorageItemBase File)
        {
            return File.StorageItem as StorageFolder;
        }

        public static class SpecialPath
        {
            public static IReadOnlyList<string> OneDrivePathCollection { get; } = new List<string>
            {
                Environment.GetEnvironmentVariable("OneDriveConsumer"),
                Environment.GetEnvironmentVariable("OneDriveCommercial"),
                Environment.GetEnvironmentVariable("OneDrive")
            };

            public enum SpecialPathEnum
            {
                OneDrive
            }

            public static bool IsPathIncluded(string Path, SpecialPathEnum Enum)
            {
                switch (Enum)
                {
                    case SpecialPathEnum.OneDrive:
                        {
                            return OneDrivePathCollection.Where((Path) => !string.IsNullOrEmpty(Path)).Any((OneDrivePath) => Path.StartsWith(OneDrivePath, StringComparison.OrdinalIgnoreCase) && !Path.Equals(OneDrivePath, StringComparison.OrdinalIgnoreCase));
                        }
                    default:
                        {
                            return false;
                        }
                }
            }
        }
    }
}
