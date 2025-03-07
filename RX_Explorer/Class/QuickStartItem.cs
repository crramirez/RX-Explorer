﻿using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace RX_Explorer.Class
{
    /// <summary>
    /// 提供对快速启动区域的UI支持
    /// </summary>
    public sealed class QuickStartItem : INotifyPropertyChanged
    {
        /// <summary>
        /// 图标
        /// </summary>
        public BitmapImage Image { get; private set; }

        /// <summary>
        /// 显示名称
        /// </summary>
        public string DisplayName { get; private set; }

        /// <summary>
        /// 图标位置
        /// </summary>
        public string IconPath { get; private set; }

        /// <summary>
        /// 快速启动项类型
        /// </summary>
        public QuickStartType Type { get; private set; }

        /// <summary>
        /// 协议或网址
        /// </summary>
        public string Protocol { get; private set; }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 更新快速启动项的信息
        /// </summary>
        /// <param name="Image">缩略图</param>
        /// <param name="Protocol">协议</param>
        /// <param name="RelativePath">图标位置</param>
        /// <param name="DisplayName">显示名称</param>
        public void Update(BitmapImage Image, string Protocol, string IconPath, string DisplayName)
        {
            this.Image = Image;
            this.Protocol = Protocol;

            this.DisplayName = DisplayName;

            if (IconPath != null)
            {
                this.IconPath = IconPath;
            }

            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Image));
        }

        private void OnPropertyChanged([CallerMemberName] string PropertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(PropertyName));
        }

        /// <summary>
        /// 初始化QuickStartItem对象
        /// </summary>
        /// <param name="Image">图标</param>
        /// <param name="Protocol">协议</param>
        /// <param name="Type">类型</param>
        /// <param name="IconPath">图标位置</param>
        /// <param name="DisplayName">显示名称</param>
        public QuickStartItem(QuickStartType Type, BitmapImage Image, string Protocol, string IconPath, string DisplayName = null)
        {
            this.Image = Image;
            this.Protocol = Protocol;
            this.Type = Type;

            this.DisplayName = DisplayName;
            this.IconPath = IconPath;
        }

        public QuickStartItem()
        {
            Type = QuickStartType.AddButton;
            AppThemeController.Current.ThemeChanged += Current_ThemeChanged;

            if (AppThemeController.Current.Theme == ElementTheme.Dark)
            {
                Image = new BitmapImage(new Uri("ms-appx:///Assets/AddImage_Light.png"));
            }
            else
            {
                Image = new BitmapImage(new Uri("ms-appx:///Assets/AddImage_Dark.png"));
            }
        }

        private void Current_ThemeChanged(object sender, ElementTheme Theme)
        {
            if (Theme == ElementTheme.Dark)
            {
                Image = new BitmapImage(new Uri("ms-appx:///Assets/AddImage_Light.png"));
            }
            else
            {
                Image = new BitmapImage(new Uri("ms-appx:///Assets/AddImage_Dark.png"));
            }

            OnPropertyChanged(nameof(Image));
        }
    }
}
