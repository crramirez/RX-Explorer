﻿using ComputerVision;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace RX_Explorer.Class
{
    /// <summary>
    /// 提供对显示应用项目的支持
    /// </summary>
    public sealed class ProgramPickerItem : IEquatable<ProgramPickerItem>
    {
        /// <summary>
        /// 默认图片
        /// </summary>
        private static readonly BitmapImage DefaultThumbnuil = new BitmapImage(AppThemeController.Current.Theme == ElementTheme.Dark ?
                                                                               new Uri("ms-appx:///Assets/Page_Solid_White.png") :
                                                                               new Uri("ms-appx:///Assets/Page_Solid_Black.png"));
        private static readonly BitmapImage InnerViewerThumbnail = new BitmapImage(new Uri("ms-appx:///Assets/RX-icon.png"));

        public static ProgramPickerItem InnerViewer { get; } = new ProgramPickerItem(InnerViewerThumbnail,
                                                                                     Globalization.GetString("ProgramPicker_Dialog_BuiltInViewer"),
                                                                                     Globalization.GetString("ProgramPicker_Dialog_BuiltInViewer_Description"),
                                                                                     Package.Current.Id.FamilyName);
        /// <summary>
        /// 应用缩略图
        /// </summary>
        public BitmapImage Thumbnuil { get; }

        /// <summary>
        /// 应用描述
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// 应用名称
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// 应用可执行程序路径或PFN
        /// </summary>
        public string Path { get; }

        public static async Task<ProgramPickerItem> CreateAsync(AppInfo App)
        {
            try
            {
                using (IRandomAccessStreamWithContentType LogoStream = await App.DisplayInfo.GetLogo(new Windows.Foundation.Size(128, 128)).OpenReadAsync())
                {
                    BitmapDecoder Decoder = await BitmapDecoder.CreateAsync(LogoStream);

                    using (SoftwareBitmap SBitmap = await Decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied))
                    using (SoftwareBitmap ResizeBitmap = ComputerVisionProvider.ResizeToActual(SBitmap))
                    using (InMemoryRandomAccessStream Stream = new InMemoryRandomAccessStream())
                    {
                        BitmapEncoder Encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, Stream);

                        Encoder.SetSoftwareBitmap(ResizeBitmap);
                        await Encoder.FlushAsync();

                        BitmapImage Logo = new BitmapImage();
                        await Logo.SetSourceAsync(Stream);

                        return new ProgramPickerItem(Logo, App.DisplayInfo.DisplayName, App.DisplayInfo.Description, App.PackageFamilyName);
                    }
                }
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex, "An exception was threw when getting or processing App Logo");
                return new ProgramPickerItem(null, App.DisplayInfo.DisplayName, App.DisplayInfo.Description, App.PackageFamilyName);
            }
        }

        public static async Task<ProgramPickerItem> CreateAsync(StorageFile ExecuteFile)
        {
            IDictionary<string, object> PropertiesDictionary = await ExecuteFile.Properties.RetrievePropertiesAsync(new string[] { "System.FileDescription" });

            string ExtraAppName = string.Empty;

            if (PropertiesDictionary.TryGetValue("System.FileDescription", out object DescriptionRaw))
            {
                ExtraAppName = Convert.ToString(DescriptionRaw);
            }

            return new ProgramPickerItem(await ExecuteFile.GetThumbnailBitmapAsync(ThumbnailMode.SingleItem),
                                         string.IsNullOrEmpty(ExtraAppName) ? ExecuteFile.DisplayName : ExtraAppName,
                                         Globalization.GetString("Application_Admin_Name"),
                                         ExecuteFile.Path);
        }

        public static async Task<ProgramPickerItem> CreateAsync(FileSystemStorageFile File)
        {
            if (await File.GetStorageItemAsync() is StorageFile ExecuteFile)
            {
                return await CreateAsync(ExecuteFile);
            }
            else
            {
                return new ProgramPickerItem(null, File.DisplayName, Globalization.GetString("Application_Admin_Name"), File.Path);
            }
        }

        public async Task<bool> LaunchAsync(string FilePath)
        {
            if (System.IO.Path.IsPathRooted(Path))
            {
                using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                {
                    return await Exclusive.Controller.RunAsync(Path, System.IO.Path.GetDirectoryName(Path), Parameters: FilePath);
                }
            }
            else
            {
                try
                {
                    StorageFile File = await StorageFile.GetFileFromPathAsync(FilePath);

                    if (await Launcher.LaunchFileAsync(File, new LauncherOptions { TargetApplicationPackageFamilyName = Path, DisplayApplicationPicker = false }))
                    {
                        return true;
                    }
                    else
                    {
                        throw new Exception();
                    }
                }
                catch (Exception)
                {
                    using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                    {
                        return await Exclusive.Controller.LaunchUWPFromPfnAsync(Path, FilePath);
                    }
                }
            }
        }

        public bool Equals(ProgramPickerItem other)
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
                    return other.Path.Equals(Path);
                }
            }
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(this, obj))
            {
                return true;
            }
            else
            {
                if (obj is ProgramPickerItem Item)
                {
                    return Item.Path.Equals(Path);
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

        public override string ToString()
        {
            return $"Name: {Name}, Path: {Path}";
        }

        /// <summary>
        /// 初始化ProgramPickerItem实例
        /// </summary>
        /// <param name="Thumbnuil">应用缩略图</param>
        /// <param name="Name">应用名称</param>
        /// <param name="Description">应用描述</param>
        /// <param name="Path">应用可执行文件路径</param>
        private ProgramPickerItem(BitmapImage Thumbnuil, string Name, string Description, string Path)
        {
            this.Thumbnuil = Thumbnuil ?? DefaultThumbnuil;
            this.Name = Name;
            this.Description = Description;
            this.Path = Path;
        }
    }
}
