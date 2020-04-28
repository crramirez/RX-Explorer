﻿using System.ComponentModel;
using Windows.Devices.Enumeration;
using Windows.UI.Xaml.Media.Imaging;

namespace FileManager.Class
{
    /// <summary>
    /// 为蓝牙模块提供蓝牙设备信息保存功能
    /// </summary>
    public sealed class BluetoothList : INotifyPropertyChanged
    {
        /// <summary>
        /// 表示蓝牙设备信息
        /// </summary>
        public DeviceInformation DeviceInfo { get; set; }

        /// <summary>
        /// 获取蓝牙设备名称
        /// </summary>
        public string Name
        {
            get
            {
                return string.IsNullOrWhiteSpace(DeviceInfo.Name) ? (Globalization.Language == LanguageEnum.Chinese ? "未知设备" : "Unknown") : DeviceInfo.Name;
            }
        }

        public BitmapImage Glyph { get; private set; }

        /// <summary>
        /// 获取蓝牙标识字符串
        /// </summary>
        public string Id
        {
            get
            {
                return DeviceInfo.Id;
            }
        }

        /// <summary>
        /// 获取配对情况描述字符串
        /// </summary>
        public string IsPaired
        {
            get
            {
                if (DeviceInfo.Pairing.IsPaired)
                {
                    return Globalization.Language == LanguageEnum.Chinese ? "已配对" : "Paired";
                }
                else
                {
                    return Globalization.Language == LanguageEnum.Chinese ? "准备配对" : "ReadyToPair";
                }
            }
        }

        /// <summary>
        /// Button显示属性
        /// </summary>
        public string CancelOrPairButton
        {
            get
            {
                if (DeviceInfo.Pairing.IsPaired)
                {
                    return Globalization.Language == LanguageEnum.Chinese ? "取消配对" : "Unpair";
                }
                else
                {
                    return Globalization.Language == LanguageEnum.Chinese ? "配对" : "Pair";
                }
            }
        }

        /// <summary>
        /// 更新蓝牙设备信息
        /// </summary>
        /// <param name="DeviceInfoUpdate">蓝牙设备的更新属性</param>
        public void Update(DeviceInformationUpdate DeviceInfoUpdate)
        {
            DeviceInfo.Update(DeviceInfoUpdate);
            OnPropertyChanged("IsPaired");
            OnPropertyChanged("Name");
        }

        public void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 创建BluetoothList的实例
        /// </summary>
        /// <param name="DeviceInfo">蓝牙设备</param>
        public BluetoothList(DeviceInformation DeviceInfo, BitmapImage Glyph)
        {
            this.DeviceInfo = DeviceInfo;
            this.Glyph = Glyph;
        }
    }
}
