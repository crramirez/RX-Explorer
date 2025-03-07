﻿using ComputerVision;
using Microsoft.Toolkit.Deferred;
using Microsoft.Toolkit.Uwp.UI.Controls;
using Microsoft.UI.Xaml.Controls;
using RX_Explorer.Class;
using RX_Explorer.Dialog;
using RX_Explorer.Interface;
using RX_Explorer.SeparateWindow.PropertyWindow;
using ShareClassLibrary;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Devices.Input;
using Windows.Devices.Radios;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;
using CommandBarFlyout = Microsoft.UI.Xaml.Controls.CommandBarFlyout;
using NavigationViewItem = Microsoft.UI.Xaml.Controls.NavigationViewItem;
using TreeViewNode = Microsoft.UI.Xaml.Controls.TreeViewNode;

namespace RX_Explorer
{
    public sealed partial class FilePresenter : Page, IDisposable
    {
        public ObservableCollection<FileSystemStorageItemBase> FileCollection { get; }
        private ObservableCollection<FileSystemStorageGroupItem> GroupCollection { get; }

        private readonly ListViewHeaderController ListViewDetailHeader = new ListViewHeaderController();

        private WeakReference<FileControl> WeakToFileControl;

        private FileControl Container
        {
            get
            {
                if (WeakToFileControl != null)
                {
                    if (WeakToFileControl.TryGetTarget(out FileControl Instance))
                    {
                        return Instance;
                    }
                    else
                    {
                        return null;
                    }
                }
                else
                {
                    return null;
                }
            }
            set
            {
                WeakToFileControl = new WeakReference<FileControl>(value);
            }
        }

        public List<ValueTuple<string, string>> GoAndBackRecord { get; } = new List<ValueTuple<string, string>>();

        public int RecordIndex { get; set; }

        private FileChangeMonitor AreaWatcher;

        private SemaphoreSlim EnterLock;
        private SemaphoreSlim CollectionChangeLock;

        private readonly PointerEventHandler PointerPressedEventHandler;
        private readonly PointerEventHandler PointerReleasedEventHandler;

        private ListViewBase itemPresenter;

        public ListViewBase ItemPresenter
        {
            get => itemPresenter;
            set
            {
                if (value != null && value != itemPresenter)
                {
                    itemPresenter?.RemoveHandler(PointerReleasedEvent, PointerReleasedEventHandler);
                    itemPresenter?.RemoveHandler(PointerPressedEvent, PointerPressedEventHandler);
                    itemPresenter = value;
                    itemPresenter.AddHandler(PointerPressedEvent, PointerPressedEventHandler, true);
                    itemPresenter.AddHandler(PointerReleasedEvent, PointerReleasedEventHandler, true);

                    SelectionExtention?.Dispose();
                    SelectionExtention = new ListViewBaseSelectionExtention(value, DrawRectangle);

                    CollectionVS.Source = IsGroupedEnable ? GroupCollection : FileCollection;

                    ViewModeSwitcher.Value = value.Name;
                }
            }
        }

        private volatile FileSystemStorageFolder currentFolder;
        public FileSystemStorageFolder CurrentFolder
        {
            get
            {
                return currentFolder;
            }
            private set
            {
                if (value != null)
                {
                    Container.UpdateAddressButton(value.Path);

                    if (value is RootStorageFolder)
                    {
                        Container.GoParentFolder.IsEnabled = false;
                    }
                    else
                    {
                        Container.GoParentFolder.IsEnabled = !value.Path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)
                                                             || !value.Path.Equals(Path.GetPathRoot(value.Path), StringComparison.OrdinalIgnoreCase);
                    }

                    Container.GlobeSearch.PlaceholderText = $"{Globalization.GetString("SearchBox_PlaceholderText")} {value.DisplayName}";
                    Container.GoBackRecord.IsEnabled = RecordIndex > 0;
                    Container.GoForwardRecord.IsEnabled = RecordIndex < GoAndBackRecord.Count - 1;
                    Container.CurrentTabItem.Header = string.IsNullOrEmpty(value.DisplayName) ? $"<{Globalization.GetString("UnknownText")}>" : value.DisplayName;

                    PageSwitcher.Value = value.Path;

                    if (this.FindParentOfType<BladeItem>() is BladeItem Parent)
                    {
                        Parent.Header = value.DisplayName;
                    }
                }

                TaskBarController.SetText(value?.DisplayName);

                currentFolder = value;
            }
        }

        private WiFiShareProvider WiFiProvider;
        private ListViewBaseSelectionExtention SelectionExtention;
        private FileSystemStorageItemBase TabTarget;
        private DateTimeOffset LastPressTime;
        private string LastPressString;
        private CancellationTokenSource DelayRenameCancellation;
        private CancellationTokenSource DelayEnterCancellation;
        private CancellationTokenSource DelaySelectionCancellation;
        private CancellationTokenSource DelayDragCancellation;
        private CancellationTokenSource DelayTooltipCancellation;
        private CancellationTokenSource ContextMenuCancellation;

        private bool GroupedEnable;

        private bool IsGroupedEnable
        {
            get
            {
                return GroupedEnable;
            }
            set
            {
                if (GroupedEnable != value)
                {
                    GroupedEnable = value;
                    CollectionVS.IsSourceGrouped = value;

                    CollectionVS.Source = value ? GroupCollection : FileCollection;
                }
            }
        }

        public FileSystemStorageItemBase SelectedItem
        {
            get
            {
                return ItemPresenter?.SelectedItem as FileSystemStorageItemBase;
            }
            set
            {
                if (ItemPresenter != null)
                {
                    ItemPresenter.SelectedItem = value;

                    if (value != null)
                    {
                        (ItemPresenter.ContainerFromItem(value) as SelectorItem)?.Focus(FocusState.Programmatic);
                    }
                }
            }
        }

        public FilePresenter(FileControl Container)
        {
            InitializeComponent();

            this.Container = Container;

            GroupCollection = new ObservableCollection<FileSystemStorageGroupItem>();

            FileCollection = new ObservableCollection<FileSystemStorageItemBase>();
            FileCollection.CollectionChanged += FileCollection_CollectionChanged;

            ListViewDetailHeader.Filter.RefreshListRequested += Filter_RefreshListRequested;

            PointerPressedEventHandler = new PointerEventHandler(ViewControl_PointerPressed);
            PointerReleasedEventHandler = new PointerEventHandler(ViewControl_PointerReleased);

            AreaWatcher = new FileChangeMonitor();
            AreaWatcher.FileChanged += DirectoryWatcher_FileChanged;

            EnterLock = new SemaphoreSlim(1, 1);
            CollectionChangeLock = new SemaphoreSlim(1, 1);

            CoreWindow Window = CoreWindow.GetForCurrentThread();
            Window.KeyDown += FilePresenter_KeyDown;
            Window.Dispatcher.AcceleratorKeyActivated += Dispatcher_AcceleratorKeyActivated;

            Loaded += FilePresenter_Loaded;
            RootFolderControl.EnterActionRequested += RootFolderControl_EnterActionRequested;

            Application.Current.Suspending += Current_Suspending;
            Application.Current.Resuming += Current_Resuming;
            SortCollectionGenerator.SortConfigChanged += Current_SortConfigChanged;
            GroupCollectionGenerator.GroupStateChanged += GroupCollectionGenerator_GroupStateChanged;
            LayoutModeController.ViewModeChanged += Current_ViewModeChanged;
        }

        private async void DirectoryWatcher_FileChanged(object sender, FileChangedDeferredEventArgs args)
        {
            EventDeferral Deferral = args.GetDeferral();

            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () =>
            {
                try
                {
                    switch (args)
                    {
                        case FileAddedDeferredEventArgs AddedArgs:
                            {
                                if (CurrentFolder.Path == Path.GetDirectoryName(AddedArgs.Path))
                                {
                                    if (FileCollection.All((Item) => !Item.Path.Equals(AddedArgs.Path, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        if (await FileSystemStorageItemBase.OpenAsync(AddedArgs.Path) is FileSystemStorageItemBase NewItem)
                                        {
                                            if (SettingPage.IsDisplayProtectedSystemItems || !NewItem.IsSystemItem)
                                            {
                                                if ((NewItem is IHiddenStorageItem && SettingPage.IsDisplayHiddenItem) || NewItem is not IHiddenStorageItem)
                                                {
                                                    if (FileCollection.Any())
                                                    {
                                                        PathConfiguration Config = SQLite.Current.GetPathConfiguration(CurrentFolder.Path);

                                                        int Index = SortCollectionGenerator.SearchInsertLocation(FileCollection, NewItem, Config.SortTarget.GetValueOrDefault(), Config.SortDirection.GetValueOrDefault());

                                                        if (Index >= 0)
                                                        {
                                                            FileCollection.Insert(Index, NewItem);
                                                        }
                                                        else
                                                        {
                                                            FileCollection.Add(NewItem);
                                                        }
                                                    }
                                                    else
                                                    {
                                                        FileCollection.Add(NewItem);
                                                    }

                                                    if (NewItem is FileSystemStorageFolder && !SettingPage.IsDetachTreeViewAndPresenter)
                                                    {
                                                        if (Container.FolderTree.RootNodes.FirstOrDefault((Node) => Path.GetPathRoot(CurrentFolder.Path).Equals((Node.Content as TreeViewNodeContent).Path, StringComparison.OrdinalIgnoreCase)) is TreeViewNode RootNode)
                                                        {
                                                            if (await RootNode.GetNodeAsync(new PathAnalysis(CurrentFolder.Path, string.Empty), true) is TreeViewNode CurrentNode)
                                                            {
                                                                await CurrentNode.UpdateAllSubNodeAsync();
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }

                                break;
                            }
                        case FileRemovedDeferredEventArgs RemovedArgs:
                            {
                                if (CurrentFolder.Path == Path.GetDirectoryName(RemovedArgs.Path))
                                {
                                    bool ShouldRefreshTreeView = false;

                                    foreach (FileSystemStorageItemBase Item in FileCollection.Where((Item) => Item.Path.Equals(RemovedArgs.Path, StringComparison.OrdinalIgnoreCase)).ToArray())
                                    {
                                        FileCollection.Remove(Item);

                                        if (Item is FileSystemStorageFolder && !SettingPage.IsDetachTreeViewAndPresenter)
                                        {
                                            ShouldRefreshTreeView = true;
                                        }
                                    }

                                    if (ShouldRefreshTreeView)
                                    {
                                        if (Container.FolderTree.RootNodes.FirstOrDefault((Node) => Path.GetPathRoot(CurrentFolder.Path).Equals((Node.Content as TreeViewNodeContent).Path, StringComparison.OrdinalIgnoreCase)) is TreeViewNode RootNode)
                                        {
                                            if (await RootNode.GetNodeAsync(new PathAnalysis(CurrentFolder.Path, string.Empty), true) is TreeViewNode CurrentNode)
                                            {
                                                await CurrentNode.UpdateAllSubNodeAsync();
                                            }
                                        }
                                    }
                                }

                                break;
                            }
                        case FileModifiedDeferredEventArgs ModifiedArgs:
                            {
                                if (CurrentFolder.Path == Path.GetDirectoryName(ModifiedArgs.Path))
                                {
                                    if (await FileSystemStorageItemBase.OpenAsync(ModifiedArgs.Path) is FileSystemStorageItemBase ModifiedItem)
                                    {
                                        PathConfiguration Config = SQLite.Current.GetPathConfiguration(CurrentFolder.Path);

                                        if (FileCollection.FirstOrDefault((Item) => Item.Path.Equals(ModifiedArgs.Path, StringComparison.OrdinalIgnoreCase)) is FileSystemStorageItemBase OldItem)
                                        {
                                            if (ModifiedItem.GetType() == OldItem.GetType())
                                            {
                                                if (IsGroupedEnable)
                                                {
                                                    if (GroupCollection.FirstOrDefault((Group) => Group.Contains(OldItem)) is FileSystemStorageGroupItem CurrentGroup)
                                                    {
                                                        string Key = GroupCollectionGenerator.SearchGroupBelonging(ModifiedItem, Config.GroupTarget.GetValueOrDefault());

                                                        if (Key != CurrentGroup.Key)
                                                        {
                                                            FileCollection.Remove(OldItem);

                                                            if (FileCollection.Any())
                                                            {
                                                                int Index = SortCollectionGenerator.SearchInsertLocation(FileCollection, ModifiedItem, Config.SortTarget.GetValueOrDefault(), Config.SortDirection.GetValueOrDefault());

                                                                if (Index >= 0)
                                                                {
                                                                    FileCollection.Insert(Index, ModifiedItem);
                                                                }
                                                                else
                                                                {
                                                                    FileCollection.Add(ModifiedItem);
                                                                }
                                                            }
                                                            else
                                                            {
                                                                FileCollection.Add(ModifiedItem);
                                                            }
                                                        }
                                                        else
                                                        {
                                                            await OldItem.RefreshAsync();
                                                        }
                                                    }
                                                    else
                                                    {
                                                        await OldItem.RefreshAsync();
                                                    }
                                                }
                                                else
                                                {
                                                    await OldItem.RefreshAsync();
                                                }
                                            }
                                            else
                                            {
                                                FileCollection.Remove(OldItem);

                                                if (SettingPage.IsDisplayProtectedSystemItems || !ModifiedItem.IsSystemItem)
                                                {
                                                    if ((ModifiedItem is IHiddenStorageItem && SettingPage.IsDisplayHiddenItem) || ModifiedItem is not IHiddenStorageItem)
                                                    {
                                                        if (FileCollection.Any())
                                                        {
                                                            int Index = SortCollectionGenerator.SearchInsertLocation(FileCollection, ModifiedItem, Config.SortTarget.GetValueOrDefault(), Config.SortDirection.GetValueOrDefault());

                                                            if (Index >= 0)
                                                            {
                                                                FileCollection.Insert(Index, ModifiedItem);
                                                            }
                                                            else
                                                            {
                                                                FileCollection.Add(ModifiedItem);
                                                            }
                                                        }
                                                        else
                                                        {
                                                            FileCollection.Add(ModifiedItem);
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        else if (ModifiedItem is not IHiddenStorageItem)
                                        {
                                            if (FileCollection.Any())
                                            {
                                                int Index = SortCollectionGenerator.SearchInsertLocation(FileCollection, ModifiedItem, Config.SortTarget.GetValueOrDefault(), Config.SortDirection.GetValueOrDefault());

                                                if (Index >= 0)
                                                {
                                                    FileCollection.Insert(Index, ModifiedItem);
                                                }
                                                else
                                                {
                                                    FileCollection.Add(ModifiedItem);
                                                }
                                            }
                                            else
                                            {
                                                FileCollection.Add(ModifiedItem);
                                            }
                                        }
                                    }
                                }

                                break;
                            }
                        case FileRenamedDeferredEventArgs RenamedArgs:
                            {
                                if (CurrentFolder.Path == Path.GetDirectoryName(RenamedArgs.Path))
                                {
                                    string NewPath = Path.Combine(CurrentFolder.Path, RenamedArgs.NewName);

                                    if (await FileSystemStorageItemBase.OpenAsync(NewPath) is FileSystemStorageItemBase Item)
                                    {
                                        if (SettingPage.IsDisplayProtectedSystemItems || !Item.IsSystemItem)
                                        {
                                            if ((Item is IHiddenStorageItem && SettingPage.IsDisplayHiddenItem) || Item is not IHiddenStorageItem)
                                            {
                                                foreach (FileSystemStorageItemBase ExistItem in FileCollection.Where((Item) => Item.Path.Equals(RenamedArgs.Path, StringComparison.OrdinalIgnoreCase)
                                                                                                                               || Item.Path.Equals(NewPath, StringComparison.OrdinalIgnoreCase)).ToArray())
                                                {
                                                    FileCollection.Remove(ExistItem);
                                                }

                                                if (FileCollection.Any())
                                                {
                                                    PathConfiguration Config = SQLite.Current.GetPathConfiguration(CurrentFolder.Path);

                                                    int Index = SortCollectionGenerator.SearchInsertLocation(FileCollection, Item, Config.SortTarget.GetValueOrDefault(), Config.SortDirection.GetValueOrDefault());

                                                    if (Index >= 0)
                                                    {
                                                        FileCollection.Insert(Index, Item);
                                                    }
                                                    else
                                                    {
                                                        FileCollection.Add(Item);
                                                    }
                                                }
                                                else
                                                {
                                                    FileCollection.Add(Item);
                                                }

                                                if (Item is FileSystemStorageFolder && !SettingPage.IsDetachTreeViewAndPresenter)
                                                {
                                                    if (Container.FolderTree.RootNodes.FirstOrDefault((Node) => Path.GetPathRoot(CurrentFolder.Path).Equals((Node.Content as TreeViewNodeContent).Path, StringComparison.OrdinalIgnoreCase)) is TreeViewNode RootNode)
                                                    {
                                                        if (await RootNode.GetNodeAsync(new PathAnalysis(CurrentFolder.Path, string.Empty), true) is TreeViewNode CurrentNode)
                                                        {
                                                            await CurrentNode.UpdateAllSubNodeAsync();
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }

                                break;
                            }
                    }
                }
                catch (Exception ex)
                {
                    LogTracer.Log(ex, $"{ nameof(FileChangeMonitor)}: Add item to collection failed");
                }
                finally
                {
                    Deferral.Complete();
                }
            });
        }

        private void GroupCollectionGenerator_GroupStateChanged(object sender, GroupCollectionGenerator.GroupStateChangedEventArgs args)
        {
            if (args.Path.Equals(CurrentFolder.Path, StringComparison.OrdinalIgnoreCase))
            {
                if (args.Target == GroupTarget.None)
                {
                    GroupAsc.IsEnabled = false;
                    GroupDesc.IsEnabled = false;

                    IsGroupedEnable = false;

                    GroupCollection.Clear();
                }
                else
                {
                    GroupAsc.IsEnabled = true;
                    GroupDesc.IsEnabled = true;

                    GroupCollection.Clear();

                    foreach (FileSystemStorageGroupItem GroupItem in GroupCollectionGenerator.GetGroupedCollection(FileCollection, args.Target, args.Direction))
                    {
                        GroupCollection.Add(GroupItem);
                    }

                    IsGroupedEnable = true;
                }
            }
        }

        private async void Dispatcher_AcceleratorKeyActivated(CoreDispatcher sender, AcceleratorKeyEventArgs args)
        {
            if (Container.CurrentPresenter == this
                && Enum.GetName(typeof(CoreAcceleratorKeyEventType), args.EventType).Contains("KeyUp")
                && args.KeyStatus.IsMenuKeyDown
                && Container.Frame.CurrentSourcePageType == typeof(FileControl)
                && Container.Frame == TabViewContainer.CurrentNavigationControl
                && MainPage.Current.NavView.SelectedItem is NavigationViewItem NavItem
                && Convert.ToString(NavItem.Content) == Globalization.GetString("MainPage_PageDictionary_Home_Label"))
            {
                switch (args.VirtualKey)
                {
                    case VirtualKey.Left:
                        {
                            args.Handled = true;

                            if (Container.GoBackRecord.IsEnabled)
                            {
                                Container.GoBackRecord_Click(null, null);
                            }

                            break;
                        }
                    case VirtualKey.Right:
                        {
                            args.Handled = true;

                            if (Container.GoForwardRecord.IsEnabled)
                            {
                                Container.GoForwardRecord_Click(null, null);
                            }

                            break;
                        }
                    case VirtualKey.Enter when ItemPresenter.SelectedItems.Count == 1:
                        {
                            args.Handled = true;

                            if (SelectedItem is FileSystemStorageItemBase Item)
                            {
                                PropertiesWindowBase NewWindow = await PropertiesWindowBase.CreateAsync(Item);
                                await NewWindow.ShowAsync(new Point(Window.Current.Bounds.Width / 2 - 200, Window.Current.Bounds.Height / 2 - 300));
                            }

                            break;
                        }
                }
            }
        }

        private async void FilePresenter_KeyDown(CoreWindow sender, KeyEventArgs args)
        {
            if (Container.CurrentPresenter == this
                && CurrentFolder is not RootStorageFolder
                && Container.Frame.CurrentSourcePageType == typeof(FileControl)
                && Container.Frame == TabViewContainer.CurrentNavigationControl
                && MainPage.Current.NavView.SelectedItem is NavigationViewItem NavItem
                && Convert.ToString(NavItem.Content) == Globalization.GetString("MainPage_PageDictionary_Home_Label"))
            {
                bool CtrlDown = sender.GetKeyState(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
                bool ShiftDown = sender.GetKeyState(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

                if (!QueueContentDialog.IsRunningOrWaiting && !Container.BlockKeyboardShortCutInput)
                {
                    if (!CtrlDown && !ShiftDown)
                    {
                        args.Handled = true;
                        NavigateToStorageItem(args.VirtualKey);
                    }

                    switch (args.VirtualKey)
                    {
                        case VirtualKey.Space when SettingPage.IsQuicklookEnabled && ItemPresenter.SelectedItems.Count <= 1:
                            {
                                args.Handled = true;

                                using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                                {
                                    if (await Exclusive.Controller.CheckIfQuicklookIsAvaliableAsync())
                                    {
                                        string ViewPathWithQuicklook = null;

                                        if (string.IsNullOrEmpty(SelectedItem?.Path))
                                        {
                                            if (!string.IsNullOrEmpty(CurrentFolder?.Path))
                                            {
                                                ViewPathWithQuicklook = CurrentFolder.Path;
                                            }
                                        }
                                        else
                                        {
                                            ViewPathWithQuicklook = SelectedItem.Path;
                                        }

                                        if (!string.IsNullOrEmpty(ViewPathWithQuicklook))
                                        {
                                            await Exclusive.Controller.ToggleQuicklookAsync(ViewPathWithQuicklook);
                                        }
                                    }
                                }

                                break;
                            }
                        case VirtualKey.F2:
                            {
                                args.Handled = true;

                                Rename_Click(null, null);
                                break;
                            }
                        case VirtualKey.F5:
                            {
                                args.Handled = true;

                                Refresh_Click(null, null);
                                break;
                            }
                        case VirtualKey.Enter when ItemPresenter.SelectedItems.Count == 1 && SelectedItem is FileSystemStorageItemBase Item:
                            {
                                args.Handled = true;

                                await EnterSelectedItemAsync(Item).ConfigureAwait(false);
                                break;
                            }
                        case VirtualKey.Back when Container.GoBackRecord.IsEnabled:
                            {
                                args.Handled = true;

                                Container.GoBackRecord_Click(null, null);
                                break;
                            }
                        case VirtualKey.L when CtrlDown:
                            {
                                args.Handled = true;

                                Container.AddressBox.Focus(FocusState.Programmatic);
                                break;
                            }
                        case VirtualKey.V when CtrlDown:
                            {
                                args.Handled = true;

                                Paste_Click(null, null);
                                break;
                            }
                        case VirtualKey.A when CtrlDown:
                            {
                                args.Handled = true;

                                ItemPresenter.SelectAll();
                                break;
                            }
                        case VirtualKey.C when CtrlDown && ShiftDown:
                            {
                                args.Handled = true;

                                Clipboard.Clear();

                                DataPackage Package = new DataPackage
                                {
                                    RequestedOperation = DataPackageOperation.Copy
                                };

                                Package.SetText(SelectedItem?.Path ?? CurrentFolder?.Path ?? string.Empty);

                                Clipboard.SetContent(Package);
                                break;
                            }
                        case VirtualKey.C when CtrlDown && ItemPresenter.SelectedItems.Count > 0:
                            {
                                args.Handled = true;

                                Copy_Click(null, null);
                                break;
                            }
                        case VirtualKey.X when CtrlDown && ItemPresenter.SelectedItems.Count > 0:
                            {
                                args.Handled = true;

                                Cut_Click(null, null);
                                break;
                            }
                        case VirtualKey.Delete when ItemPresenter.SelectedItems.Count > 0:
                        case VirtualKey.D when CtrlDown && ItemPresenter.SelectedItems.Count > 0:
                            {
                                args.Handled = true;

                                Delete_Click(null, null);
                                break;
                            }
                        case VirtualKey.F when CtrlDown:
                            {
                                args.Handled = true;

                                Container.GlobeSearch.Focus(FocusState.Programmatic);
                                break;
                            }
                        case VirtualKey.N when CtrlDown && ShiftDown:
                            {
                                args.Handled = true;

                                CreateFolder_Click(null, null);
                                break;
                            }
                        case VirtualKey.Z when CtrlDown && OperationRecorder.Current.IsNotEmpty:
                            {
                                args.Handled = true;

                                await ExecuteUndoAsync();
                                break;
                            }
                        case VirtualKey.E when ShiftDown && CurrentFolder != null:
                            {
                                args.Handled = true;

                                await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                                break;
                            }
                        case VirtualKey.T when ShiftDown:
                            {
                                args.Handled = true;

                                OpenInTerminal_Click(null, null);
                                break;
                            }
                        case VirtualKey.T when CtrlDown && ItemPresenter.SelectedItems.Count <= 1:
                            {
                                args.Handled = true;

                                CloseAllFlyout();

                                if (SelectedItem is FileSystemStorageFolder)
                                {
                                    await TabViewContainer.Current.CreateNewTabAsync(SelectedItem.Path);
                                }
                                else
                                {
                                    await TabViewContainer.Current.CreateNewTabAsync();
                                }

                                break;
                            }
                        case VirtualKey.Q when CtrlDown && ItemPresenter.SelectedItems.Count == 1:
                            {
                                args.Handled = true;

                                OpenFolderInNewWindow_Click(null, null);
                                break;
                            }
                        case VirtualKey.Up when SelectedItem == null:
                        case VirtualKey.Down when SelectedItem == null:
                            {
                                args.Handled = true;

                                SelectedItem = FileCollection.FirstOrDefault();
                                break;
                            }
                        case VirtualKey.B when CtrlDown:
                            {
                                args.Handled = true;

                                if (await MSStoreHelper.Current.CheckPurchaseStatusAsync())
                                {
                                    if (ItemPresenter.SelectedItems.Count == 1 && SelectedItem is FileSystemStorageFolder Folder)
                                    {
                                        await Container.CreateNewBladeAsync(Folder.Path);
                                    }
                                    else
                                    {
                                        await Container.CreateNewBladeAsync(CurrentFolder.Path);
                                    }
                                }

                                break;
                            }
                    }
                }
            }
        }

        private void Current_ViewModeChanged(object sender, LayoutModeChangedEventArgs e)
        {
            if ((e.Path?.Equals(CurrentFolder?.Path, StringComparison.OrdinalIgnoreCase)).GetValueOrDefault())
            {
                if (e.Index >= 0 && e.Index < LayoutModeController.ItemsSource.Count)
                {
                    try
                    {
                        ItemPresenter = e.Index switch
                        {
                            0 => GridViewTilesControl,
                            1 => ListViewControl,
                            2 => GridViewListControl,
                            3 => GridViewLargeIconControl,
                            4 => GridViewMediumIconControl,
                            5 => GridViewSmallIconControl,
                            _ => throw new ArgumentException($"Value: {e.Index} is out of range", nameof(e.Index))
                        };

                        SQLite.Current.SetPathConfiguration(new PathConfiguration(CurrentFolder.Path, e.Index));
                    }
                    catch (Exception ex)
                    {
                        LogTracer.Log(ex, "Switch DisplayMode could not be completed successfully");
                    }
                }
            }
        }

        private void Current_SortConfigChanged(object sender, SortCollectionGenerator.SortStateChangedEventArgs args)
        {
            if (args.Path.Equals(CurrentFolder.Path, StringComparison.OrdinalIgnoreCase))
            {
                ListViewDetailHeader.Indicator.SetIndicatorStatus(args.Target, args.Direction);

                if (IsGroupedEnable)
                {
                    foreach (FileSystemStorageGroupItem GroupItem in GroupCollection)
                    {
                        FileSystemStorageItemBase[] SortedGroupItem = SortCollectionGenerator.GetSortedCollection(GroupItem, args.Target, args.Direction).ToArray();

                        GroupItem.Clear();

                        foreach (FileSystemStorageItemBase Item in SortedGroupItem)
                        {
                            GroupItem.Add(Item);
                        }
                    }
                }

                FileSystemStorageItemBase[] ItemList = SortCollectionGenerator.GetSortedCollection(FileCollection, args.Target, args.Direction).ToArray();

                FileCollection.Clear();

                foreach (FileSystemStorageItemBase Item in ItemList)
                {
                    FileCollection.Add(Item);
                }
            }
        }

        private async Task DisplayItemsInFolderCore(FileSystemStorageFolder Folder, bool ForceRefresh = false, bool SkipNavigationRecord = false)
        {
            await EnterLock.WaitAsync();

            try
            {
                if (Folder == null)
                {
                    throw new ArgumentNullException(nameof(Folder), "Parameter could not be null");
                }

                if (ForceRefresh || Folder.Path != (CurrentFolder?.Path))
                {
                    if (!SkipNavigationRecord && !ForceRefresh)
                    {
                        if (GoAndBackRecord.Count > 0)
                        {
                            if (RecordIndex != GoAndBackRecord.Count - 1)
                            {
                                GoAndBackRecord.RemoveRange(RecordIndex + 1, GoAndBackRecord.Count - RecordIndex - 1);
                            }

                            string ParentPath = Path.GetDirectoryName(Folder.Path);

                            if (!string.IsNullOrEmpty(ParentPath))
                            {
                                if (ParentPath.Equals(GoAndBackRecord[GoAndBackRecord.Count - 1].Item1, StringComparison.OrdinalIgnoreCase))
                                {
                                    GoAndBackRecord[GoAndBackRecord.Count - 1] = (ParentPath, Folder.Path);
                                }
                                else
                                {
                                    GoAndBackRecord[GoAndBackRecord.Count - 1] = (GoAndBackRecord[GoAndBackRecord.Count - 1].Item1, (ItemPresenter?.SelectedItems.Count).GetValueOrDefault() > 1 ? string.Empty : ((SelectedItem?.Path) ?? string.Empty));
                                }
                            }
                        }

                        GoAndBackRecord.Add((Folder.Path, string.Empty));

                        RecordIndex = GoAndBackRecord.Count - 1;
                    }

                    DelayDragCancellation?.Cancel();
                    DelayEnterCancellation?.Cancel();
                    DelayRenameCancellation?.Cancel();
                    DelaySelectionCancellation?.Cancel();
                    DelayTooltipCancellation?.Cancel();

                    if (Folder is RootStorageFolder)
                    {
                        CurrentFolder = RootStorageFolder.Instance;
                        TabViewContainer.Current.LayoutModeControl.IsEnabled = false;
                        FileCollection.Clear();
                        GroupCollection.Clear();

                        await AreaWatcher.StopMonitorAsync();
                    }
                    else if (await FileSystemStorageItemBase.CheckExistAsync(Folder.Path))
                    {
                        //If target is network path and the user had already mapped it as drive, then we should remap the network path to the drive path if possible.
                        //Use drive path could get more benefit from loading speed and directory monitor
                        if (Folder.Path.StartsWith(@"\\"))
                        {
                            IReadOnlyList<DriveDataBase> NetworkDriveList = CommonAccessCollection.DriveList.Where((Drive) => Drive.DriveType == DriveType.Network).ToList();

                            if (NetworkDriveList.Count > 0)
                            {
                                string RemappedPath = await UncPath.MapUncToDrivePath(NetworkDriveList.Select((Drive) => Drive.Path), Folder.Path);

                                if (await FileSystemStorageItemBase.OpenAsync(RemappedPath) is FileSystemStorageFolder RemappedFolder)
                                {
                                    Folder = RemappedFolder;
                                }
                            }
                        }

                        CurrentFolder = Folder;

                        if (Container.FolderTree.SelectedNode == null
                            && Container.FolderTree.RootNodes.FirstOrDefault((Node) => Path.GetPathRoot(Folder.Path).Equals((Node.Content as TreeViewNodeContent)?.Path, StringComparison.OrdinalIgnoreCase)) is TreeViewNode RootNode)
                        {
                            Container.FolderTree.SelectNodeAndScrollToVertical(RootNode);
                        }

                        FileCollection.Clear();
                        GroupCollection.Clear();

                        PathConfiguration Config = SQLite.Current.GetPathConfiguration(Folder.Path);

                        TabViewContainer.Current.LayoutModeControl.IsEnabled = true;
                        TabViewContainer.Current.LayoutModeControl.CurrentPath = Config.Path;
                        TabViewContainer.Current.LayoutModeControl.ViewModeIndex = Config.DisplayModeIndex.GetValueOrDefault();

                        IReadOnlyList<FileSystemStorageItemBase> ChildItems = await CurrentFolder.GetChildItemsAsync(SettingPage.IsDisplayHiddenItem, SettingPage.IsDisplayProtectedSystemItems);

                        if (ChildItems.Count > 0)
                        {
                            HasFile.Visibility = Visibility.Collapsed;

                            if (Config.GroupTarget != GroupTarget.None)
                            {
                                foreach (FileSystemStorageGroupItem GroupItem in GroupCollectionGenerator.GetGroupedCollection(ChildItems, Config.GroupTarget.GetValueOrDefault(), Config.GroupDirection.GetValueOrDefault()))
                                {
                                    GroupCollection.Add(new FileSystemStorageGroupItem(GroupItem.Key, SortCollectionGenerator.GetSortedCollection(GroupItem, Config.SortTarget.GetValueOrDefault(), Config.SortDirection.GetValueOrDefault())));
                                }

                                IsGroupedEnable = true;
                            }
                            else
                            {
                                IsGroupedEnable = false;
                            }

                            FileCollection.AddRange(SortCollectionGenerator.GetSortedCollection(ChildItems, Config.SortTarget.GetValueOrDefault(), Config.SortDirection.GetValueOrDefault()));
                        }
                        else
                        {
                            HasFile.Visibility = Visibility.Visible;
                        }

                        StatusTips.Text = Globalization.GetString("FilePresenterBottomStatusTip_TotalItem").Replace("{ItemNum}", FileCollection.Count.ToString());

                        ListViewDetailHeader.Filter.SetDataSource(FileCollection);
                        ListViewDetailHeader.Indicator.SetIndicatorStatus(Config.SortTarget.GetValueOrDefault(), Config.SortDirection.GetValueOrDefault());

                        await AreaWatcher.StartMonitorAsync(Folder.Path);
                    }
                    else
                    {
                        throw new FileNotFoundException();
                    }
                }
            }
            finally
            {
                EnterLock.Release();
            }
        }

        public async Task<bool> DisplayItemsInFolder(FileSystemStorageFolder Folder, bool ForceRefresh = false, bool SkipNavigationRecord = false)
        {
            if (Folder == null)
            {
                throw new ArgumentNullException(nameof(Folder), "Parameter could not be null or empty");
            }

            try
            {
                await DisplayItemsInFolderCore(Folder, ForceRefresh, SkipNavigationRecord);
                return true;
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex, $"Could not display items in folder: \"{Folder.Path}\"");
                return false;
            }
        }

        public async Task<bool> DisplayItemsInFolder(string FolderPath, bool ForceRefresh = false, bool SkipNavigationRecord = false)
        {
            if (RootStorageFolder.Instance.Path.Equals(FolderPath, StringComparison.OrdinalIgnoreCase))
            {
                return await DisplayItemsInFolder(RootStorageFolder.Instance, ForceRefresh, SkipNavigationRecord);
            }
            else
            {
                if (await FileSystemStorageItemBase.OpenAsync(FolderPath) is FileSystemStorageFolder Folder)
                {
                    try
                    {
                        await DisplayItemsInFolderCore(Folder, ForceRefresh, SkipNavigationRecord);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LogTracer.Log(ex, $"Could not display items in folder: \"{FolderPath}\"");
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }
        }

        private void Presenter_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            int Delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;

            if (Window.Current.CoreWindow.GetKeyState(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down))
            {
                if (Delta > 0)
                {
                    if (TabViewContainer.Current.LayoutModeControl.ViewModeIndex > 0)
                    {
                        TabViewContainer.Current.LayoutModeControl.ViewModeIndex--;
                    }
                }
                else
                {
                    if (TabViewContainer.Current.LayoutModeControl.ViewModeIndex < LayoutModeController.ItemsSource.Count - 1)
                    {
                        TabViewContainer.Current.LayoutModeControl.ViewModeIndex++;
                    }
                }

                e.Handled = true;
            }
        }

        private async void Current_Resuming(object sender, object e)
        {
            await AreaWatcher.StartMonitorAsync(AreaWatcher.CurrentLocation);
        }

        private async void Current_Suspending(object sender, SuspendingEventArgs e)
        {
            SuspendingDeferral Deferral = e.SuspendingOperation.GetDeferral();

            try
            {
                await AreaWatcher.StopMonitorAsync();
            }
            finally
            {
                Deferral.Complete();
            }
        }

        private async void FilePresenter_Loaded(object sender, RoutedEventArgs e)
        {
            if (this.FindParentOfType<BladeItem>() is BladeItem Parent)
            {
                Parent.Header = CurrentFolder?.DisplayName;
            }

            if (await MSStoreHelper.Current.CheckPurchaseStatusAsync())
            {
                OpenFolderInVerticalSplitView.Visibility = Visibility.Visible;
            }
        }

        private void NavigateToStorageItem(VirtualKey Key)
        {
            if (Key >= VirtualKey.Number0 && Key <= VirtualKey.Z)
            {
                string SearchString = Convert.ToChar(Key).ToString();

                try
                {
                    if (LastPressString != SearchString && (DateTimeOffset.Now - LastPressTime).TotalMilliseconds < 1200)
                    {
                        SearchString = LastPressString + SearchString;

                        IEnumerable<FileSystemStorageItemBase> Group = FileCollection.Where((Item) => Item.Name.StartsWith(SearchString, StringComparison.OrdinalIgnoreCase));

                        if (Group.Any() && (SelectedItem == null || !Group.Contains(SelectedItem)))
                        {
                            SelectedItem = Group.FirstOrDefault();
                            ItemPresenter.ScrollIntoView(SelectedItem);
                        }
                    }
                    else
                    {
                        IEnumerable<FileSystemStorageItemBase> Group = FileCollection.Where((Item) => Item.Name.StartsWith(SearchString, StringComparison.OrdinalIgnoreCase));

                        if (Group.Any())
                        {
                            if (SelectedItem != null)
                            {
                                FileSystemStorageItemBase[] ItemArray = Group.ToArray();

                                int NextIndex = Array.IndexOf(ItemArray, SelectedItem);

                                if (NextIndex != -1)
                                {
                                    if (NextIndex < ItemArray.Length - 1)
                                    {
                                        SelectedItem = ItemArray[NextIndex + 1];
                                    }
                                    else
                                    {
                                        SelectedItem = ItemArray.FirstOrDefault();
                                    }
                                }
                                else
                                {
                                    SelectedItem = ItemArray.FirstOrDefault();
                                }
                            }
                            else
                            {
                                SelectedItem = Group.FirstOrDefault();
                            }

                            ItemPresenter.ScrollIntoView(SelectedItem);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogTracer.Log(ex, $"{nameof(NavigateToStorageItem)} throw an exception");
                }
                finally
                {
                    LastPressString = SearchString;
                    LastPressTime = DateTimeOffset.Now;
                }
            }
        }

        private async void FileCollection_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            await CollectionChangeLock.WaitAsync();

            try
            {
                PathConfiguration Config = SQLite.Current.GetPathConfiguration(CurrentFolder.Path);

                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                        {
                            IEnumerable<FileSystemStorageItemBase> GroupExpandedCollection = GroupCollection.SelectMany((Group) => Group);

                            foreach (FileSystemStorageItemBase Item in e.NewItems)
                            {
                                if (GroupExpandedCollection.All((ExistItem) => ExistItem != Item))
                                {
                                    string Key = GroupCollectionGenerator.SearchGroupBelonging(Item, Config.GroupTarget.GetValueOrDefault());

                                    if (GroupCollection.FirstOrDefault((Item) => Item.Key == Key) is FileSystemStorageGroupItem GroupItem)
                                    {
                                        int Index = SortCollectionGenerator.SearchInsertLocation(GroupItem, Item, Config.SortTarget.GetValueOrDefault(), Config.SortDirection.GetValueOrDefault());

                                        if (Index >= 0)
                                        {
                                            GroupItem.Insert(Index, Item);
                                        }
                                        else
                                        {
                                            GroupItem.Add(Item);
                                        }
                                    }
                                }
                            }

                            break;
                        }
                    case NotifyCollectionChangedAction.Remove:
                        {
                            foreach (FileSystemStorageItemBase Item in e.OldItems)
                            {
                                if (GroupCollection.FirstOrDefault((Group) => Group.Contains(Item)) is FileSystemStorageGroupItem GroupItem)
                                {
                                    GroupItem.Remove(Item);
                                }
                            }

                            break;
                        }
                    case NotifyCollectionChangedAction.Replace:
                        {
                            IEnumerable<FileSystemStorageItemBase> GroupExpandedCollection = GroupCollection.SelectMany((Group) => Group);

                            foreach (FileSystemStorageItemBase Item in e.OldItems)
                            {
                                if (GroupExpandedCollection.Any((ExistItem) => ExistItem == Item))
                                {
                                    string Key = GroupCollectionGenerator.SearchGroupBelonging(Item, Config.GroupTarget.GetValueOrDefault());

                                    if (GroupCollection.FirstOrDefault((Item) => Item.Key == Key) is FileSystemStorageGroupItem GroupItem)
                                    {
                                        GroupItem.Remove(Item);
                                    }
                                }
                            }

                            foreach (FileSystemStorageItemBase Item in e.NewItems)
                            {
                                if (GroupExpandedCollection.All((ExistItem) => ExistItem != Item))
                                {
                                    string Key = GroupCollectionGenerator.SearchGroupBelonging(Item, Config.GroupTarget.GetValueOrDefault());

                                    if (GroupCollection.FirstOrDefault((Item) => Item.Key == Key) is FileSystemStorageGroupItem GroupItem)
                                    {
                                        int Index = SortCollectionGenerator.SearchInsertLocation(GroupItem, Item, Config.SortTarget.GetValueOrDefault(), Config.SortDirection.GetValueOrDefault());

                                        if (Index >= 0)
                                        {
                                            GroupItem.Insert(Index, Item);
                                        }
                                        else
                                        {
                                            GroupItem.Add(Item);
                                        }
                                    }
                                }
                            }

                            break;
                        }
                }

                if (e.Action != NotifyCollectionChangedAction.Reset)
                {
                    HasFile.Visibility = FileCollection.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex);
            }
            finally
            {
                CollectionChangeLock.Release();
            }
        }

        /// <summary>
        /// 关闭右键菜单
        /// </summary>
        private void CloseAllFlyout()
        {
            try
            {
                FileFlyout.Hide();
                FolderFlyout.Hide();
                EmptyFlyout.Hide();
                MixedFlyout.Hide();
                LinkItemFlyout.Hide();
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex, "Could not close the flyout for unknown reason");
            }
        }

        private async Task ExecuteUndoAsync()
        {
            try
            {
                IReadOnlyList<string> RecordList = OperationRecorder.Current.Pop();

                if (RecordList.Count > 0)
                {
                    IEnumerable<string[]> SplitGroup = RecordList.Select((Item) => Item.Split("||", StringSplitOptions.RemoveEmptyEntries));

                    IEnumerable<string> OriginFolderPathList = SplitGroup.Select((Item) => Path.GetDirectoryName(Item[0]));

                    string OriginFolderPath = OriginFolderPathList.FirstOrDefault();

                    if (OriginFolderPathList.All((Item) => Item.Equals(OriginFolderPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        IEnumerable<string> UndoModeList = SplitGroup.Select((Item) => Item[1]);

                        string UndoMode = UndoModeList.FirstOrDefault();

                        if (UndoModeList.All((Mode) => Mode.Equals(UndoMode, StringComparison.OrdinalIgnoreCase)))
                        {
                            switch (UndoMode)
                            {
                                case "Delete":
                                    {
                                        QueueTaskController.EnqueueDeleteUndoOpeartion(SplitGroup.Select((Item) => Item[0]).ToArray(), OnCompleted: async (s, e) =>
                                        {
                                            if (!SettingPage.IsDetachTreeViewAndPresenter)
                                            {
                                                foreach (TreeViewNode RootNode in Container.FolderTree.RootNodes.Where((Node) => !(Node.Content as TreeViewNodeContent).Path.Equals("QuickAccessPath", StringComparison.OrdinalIgnoreCase)))
                                                {
                                                    await RootNode.UpdateAllSubNodeAsync();
                                                }
                                            }
                                        });

                                        break;
                                    }
                                case "Move":
                                    {
                                        Dictionary<string, string> Dic = new Dictionary<string, string>();

                                        foreach (string[] Group in SplitGroup)
                                        {
                                            Dic.Add(Group[2], Path.GetFileName(Group[0]));
                                        }

                                        QueueTaskController.EnqueueMoveUndoOpeartion(Dic, OriginFolderPath, OnCompleted: async (s, e) =>
                                        {
                                            if (!SettingPage.IsDetachTreeViewAndPresenter)
                                            {
                                                foreach (TreeViewNode RootNode in Container.FolderTree.RootNodes.Where((Node) => !(Node.Content as TreeViewNodeContent).Path.Equals("QuickAccessPath", StringComparison.OrdinalIgnoreCase)))
                                                {
                                                    await RootNode.UpdateAllSubNodeAsync();
                                                }
                                            }
                                        });

                                        break;
                                    }
                                case "Copy":
                                    {
                                        QueueTaskController.EnqueueCopyUndoOpeartion(SplitGroup.Select((Item) => Item[2]).ToArray(), OnCompleted: async (s, e) =>
                                        {
                                            if (!SettingPage.IsDetachTreeViewAndPresenter)
                                            {
                                                foreach (TreeViewNode RootNode in Container.FolderTree.RootNodes.Where((Node) => !(Node.Content as TreeViewNodeContent).Path.Equals("QuickAccessPath", StringComparison.OrdinalIgnoreCase)))
                                                {
                                                    await RootNode.UpdateAllSubNodeAsync();
                                                }
                                            }
                                        });

                                        break;
                                    }
                                case "Rename":
                                    {
                                        QueueTaskController.EnqueueRenameUndoOpeartion(SplitGroup.Select((Item) => Item[2]).First(), SplitGroup.Select((Item) => Item[0]).First(), OnCompleted: async (s, e) =>
                                        {
                                            if (!SettingPage.IsDetachTreeViewAndPresenter)
                                            {
                                                foreach (TreeViewNode RootNode in Container.FolderTree.RootNodes.Where((Node) => !(Node.Content as TreeViewNodeContent).Path.Equals("QuickAccessPath", StringComparison.OrdinalIgnoreCase)))
                                                {
                                                    await RootNode.UpdateAllSubNodeAsync();
                                                }
                                            }
                                        });

                                        break;
                                    }
                                case "New":
                                    {
                                        QueueTaskController.EnqueueNewUndoOpeartion(SplitGroup.Select((Item) => Item[0]).First(), OnCompleted: async (s, e) =>
                                        {
                                            if (!SettingPage.IsDetachTreeViewAndPresenter)
                                            {
                                                foreach (TreeViewNode RootNode in Container.FolderTree.RootNodes.Where((Node) => !(Node.Content as TreeViewNodeContent).Path.Equals("QuickAccessPath", StringComparison.OrdinalIgnoreCase)))
                                                {
                                                    await RootNode.UpdateAllSubNodeAsync();
                                                }
                                            }
                                        });

                                        break;
                                    }
                            }
                        }
                        else
                        {
                            throw new Exception("Undo data format is invalid");
                        }
                    }
                    else
                    {
                        throw new Exception("Undo data format is invalid");
                    }
                }
                else
                {
                    throw new Exception("Undo data format is invalid");
                }
            }
            catch
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_WarningTitle"),
                    Content = Globalization.GetString("QueueDialog_UndoFailure_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync();
            }
        }

        private async void Copy_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            IReadOnlyList<FileSystemStorageItemBase> SelectedItemsCopy = ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().ToList();

            if (SelectedItemsCopy.Count > 0)
            {
                try
                {
                    Clipboard.Clear();
                    Clipboard.SetContent(await SelectedItemsCopy.GetAsDataPackageAsync(DataPackageOperation.Copy));

                    FileCollection.Where((Item) => Item.ThumbnailOpacity != 1d).ToList().ForEach((Item) => Item.SetThumbnailOpacity(ThumbnailStatus.Normal));
                }
                catch
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnableAccessClipboard_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    await Dialog.ShowAsync();
                }
            }
        }

        private async void Paste_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            try
            {
                DataPackageView Package = Clipboard.GetContent();

                IReadOnlyList<string> PathList = await Package.GetAsPathListAsync();

                if (PathList.Count > 0)
                {
                    if (Package.RequestedOperation.HasFlag(DataPackageOperation.Move))
                    {
                        if (PathList.All((Path) => System.IO.Path.GetDirectoryName(Path) != CurrentFolder.Path))
                        {
                            QueueTaskController.EnqueueMoveOpeartion(PathList, CurrentFolder.Path);
                        }
                    }
                    else
                    {
                        QueueTaskController.EnqueueCopyOpeartion(PathList, CurrentFolder.Path);
                    }
                }
            }
            catch (Exception ex) when (ex.HResult is unchecked((int)0x80040064) or unchecked((int)0x8004006A))
            {
                QueueTaskController.EnqueueRemoteCopyOpeartion(CurrentFolder.Path);
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex, "Could not paste the item");

                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_FailToGetClipboardError_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                await dialog.ShowAsync();
            }
            finally
            {
                FileCollection.Where((Item) => Item.ThumbnailOpacity != 1d).ToList().ForEach((Item) => Item.SetThumbnailOpacity(ThumbnailStatus.Normal));
            }
        }

        private async void Cut_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            IReadOnlyList<FileSystemStorageItemBase> SelectedItemsCopy = ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().ToList();

            if (SelectedItemsCopy.Count > 0)
            {
                try
                {
                    Clipboard.Clear();
                    Clipboard.SetContent(await SelectedItemsCopy.GetAsDataPackageAsync(DataPackageOperation.Move));

                    FileCollection.Where((Item) => Item.ThumbnailOpacity != 1d).ToList().ForEach((Item) => Item.SetThumbnailOpacity(ThumbnailStatus.Normal));

                    foreach (FileSystemStorageItemBase Item in SelectedItemsCopy)
                    {
                        Item.SetThumbnailOpacity(ThumbnailStatus.ReducedOpacity);
                    }
                }
                catch
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnableAccessClipboard_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    await Dialog.ShowAsync();
                }
            }
        }

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (ItemPresenter.SelectedItems.Count > 0)
            {
                //We should take the path of what we want to delete first. Or we might delete some items incorrectly
                string[] PathList = ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().Select((Item) => Item.Path).ToArray();

                bool ExecuteDelete = false;
                bool PermanentDelete = Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

                if (ApplicationData.Current.LocalSettings.Values["DeleteConfirmSwitch"] is bool DeleteConfirm)
                {
                    if (DeleteConfirm)
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_WarningTitle"),
                            PrimaryButtonText = Globalization.GetString("Common_Dialog_ContinueButton"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton"),
                            Content = PermanentDelete ? Globalization.GetString("QueueDialog_DeleteFilesPermanent_Content") : Globalization.GetString("QueueDialog_DeleteFiles_Content")
                        };

                        if (await Dialog.ShowAsync() == ContentDialogResult.Primary)
                        {
                            ExecuteDelete = true;
                        }
                    }
                    else
                    {
                        ExecuteDelete = true;
                    }
                }
                else
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_WarningTitle"),
                        PrimaryButtonText = Globalization.GetString("Common_Dialog_ContinueButton"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton"),
                        Content = PermanentDelete ? Globalization.GetString("QueueDialog_DeleteFilesPermanent_Content") : Globalization.GetString("QueueDialog_DeleteFiles_Content")
                    };

                    if (await Dialog.ShowAsync() == ContentDialogResult.Primary)
                    {
                        ExecuteDelete = true;
                    }
                }

                if (ExecuteDelete)
                {
                    if (ApplicationData.Current.LocalSettings.Values["AvoidRecycleBin"] is bool IsAvoidRecycleBin)
                    {
                        PermanentDelete |= IsAvoidRecycleBin;
                    }

                    foreach ((TabViewItem Tab, BladeItem[] Blades) in TabViewContainer.Current.TabCollection.Where((Tab) => Tab.Tag is FileControl)
                                                                                                             .Select((Tab) => (Tab, (Tab.Tag as FileControl).BladeViewer.Items.Cast<BladeItem>().ToArray())).ToArray())
                    {
                        foreach (string DeletePath in PathList)
                        {
                            if (Blades.Select((BItem) => (BItem.Content as FilePresenter)?.CurrentFolder?.Path)
                                      .All((BladePath) => BladePath.StartsWith(DeletePath, StringComparison.OrdinalIgnoreCase)))
                            {
                                await TabViewContainer.Current.CleanUpAndRemoveTabItem(Tab);
                            }
                            else
                            {
                                foreach (BladeItem BItem in Blades.Where((Item) => ((Item.Content as FilePresenter).CurrentFolder?.Path.StartsWith(DeletePath, StringComparison.OrdinalIgnoreCase)).GetValueOrDefault()))
                                {
                                    await (Tab.Tag as FileControl).CloseBladeAsync(BItem);
                                }
                            }
                        }
                    }

                    QueueTaskController.EnqueueDeleteOpeartion(PathList, PermanentDelete);
                }
            }
        }

        private async void Rename_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            IReadOnlyList<FileSystemStorageItemBase> SelectedItemsCopy = ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().ToList();

            if (SelectedItemsCopy.Count > 0)
            {
                RenameDialog dialog = new RenameDialog(SelectedItemsCopy);

                if ((await dialog.ShowAsync()) == ContentDialogResult.Primary)
                {
                    try
                    {
                        if (SelectedItemsCopy.Count == 1)
                        {
                            string OriginName = SelectedItemsCopy[0].Name;
                            string NewName = dialog.DesireNameMap[OriginName];

                            if (!OriginName.Equals(NewName, StringComparison.OrdinalIgnoreCase)
                                && await FileSystemStorageItemBase.CheckExistAsync(Path.Combine(CurrentFolder.Path, NewName)))
                            {
                                QueueContentDialog Dialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                    Content = Globalization.GetString("QueueDialog_RenameExist_Content"),
                                    PrimaryButtonText = Globalization.GetString("Common_Dialog_ContinueButton"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                };

                                if (await Dialog.ShowAsync() != ContentDialogResult.Primary)
                                {
                                    return;
                                }
                            }

                            await SelectedItemsCopy[0].RenameAsync(NewName);

                            FileSystemStorageItemBase TargetItem = null;

                            for (int MaxSearchLimit = 0; MaxSearchLimit < 4; MaxSearchLimit++)
                            {
                                TargetItem = FileCollection.FirstOrDefault((Item) => Item.Name.Equals(NewName, StringComparison.OrdinalIgnoreCase));

                                if (TargetItem == null)
                                {
                                    await Task.Delay(500);
                                }
                                else
                                {
                                    SelectedItem = TargetItem;
                                    ItemPresenter.ScrollIntoView(TargetItem);
                                    break;
                                }
                            }
                        }
                        else
                        {
                            foreach (FileSystemStorageItemBase OriginItem in SelectedItemsCopy)
                            {
                                await OriginItem.RenameAsync(dialog.DesireNameMap[OriginItem.Name]);
                            }
                        }
                    }
                    catch (FileLoadException)
                    {
                        QueueContentDialog LoadExceptionDialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_FileOccupied_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton"),
                        };

                        _ = await LoadExceptionDialog.ShowAsync();
                    }
                    catch (Exception)
                    {
                        QueueContentDialog UnauthorizeDialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_UnauthorizedRenameFile_Content"),
                            PrimaryButtonText = Globalization.GetString("Common_Dialog_ConfirmButton"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                        };

                        await UnauthorizeDialog.ShowAsync();
                    }
                }
            }
        }

        private async void BluetoothShare_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (await SelectedItem.GetStorageItemAsync() is StorageFile ShareFile)
            {
                if (!await FileSystemStorageItemBase.CheckExistAsync(ShareFile.Path))
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync();

                    return;
                }

                IReadOnlyList<Radio> RadioDevice = await Radio.GetRadiosAsync();

                if (RadioDevice.Any((Device) => Device.Kind == RadioKind.Bluetooth && Device.State == RadioState.On))
                {
                    BluetoothUI Bluetooth = new BluetoothUI();
                    if ((await Bluetooth.ShowAsync()) == ContentDialogResult.Primary)
                    {
                        BluetoothFileTransfer FileTransfer = new BluetoothFileTransfer(ShareFile);

                        _ = await FileTransfer.ShowAsync();
                    }
                }
                else
                {
                    QueueContentDialog dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_TipTitle"),
                        Content = Globalization.GetString("QueueDialog_OpenBluetooth_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };
                    _ = await dialog.ShowAsync();
                }
            }
            else
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_UnableAccessFile_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync();
            }
        }

        private async void ViewControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            DelayRenameCancellation?.Cancel();

            IReadOnlyList<FileSystemStorageItemBase> SelectedItemsCopy = ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().ToList();

            if (SelectedItemsCopy.Count == 1 && SelectedItemsCopy.First() is FileSystemStorageFile File)
            {
                FileEdit.IsEnabled = false;
                FileShare.IsEnabled = true;

                ChooseOtherApp.IsEnabled = true;
                RunWithSystemAuthority.IsEnabled = false;

                switch (File.Type.ToLower())
                {
                    case ".mp4":
                    case ".wmv":
                        {
                            FileEdit.IsEnabled = true;
                            Transcode.IsEnabled = true;
                            VideoEdit.IsEnabled = true;
                            VideoMerge.IsEnabled = true;
                            break;
                        }
                    case ".mkv":
                    case ".m4a":
                    case ".mov":
                    case ".mp3":
                    case ".flac":
                    case ".wma":
                    case ".alac":
                    case ".png":
                    case ".bmp":
                    case ".jpg":
                    case ".heic":
                    case ".tiff":
                        {
                            FileEdit.IsEnabled = true;
                            VideoEdit.IsEnabled = false;
                            VideoMerge.IsEnabled = false;
                            Transcode.IsEnabled = true;
                            break;
                        }
                    case ".exe":
                        {
                            ChooseOtherApp.IsEnabled = false;
                            RunWithSystemAuthority.IsEnabled = true;
                            break;
                        }
                    case ".msi":
                    case ".bat":
                        {
                            RunWithSystemAuthority.IsEnabled = true;
                            break;
                        }
                    case ".msc":
                        {
                            ChooseOtherApp.IsEnabled = false;
                            break;
                        }
                }
            }

            string[] StatusTipsSplit = StatusTips.Text.Split("  |  ", StringSplitOptions.RemoveEmptyEntries);

            if (SelectedItemsCopy.Count > 0)
            {
                string SizeInfo = string.Empty;

                if (SelectedItemsCopy.All((Item) => Item is FileSystemStorageFile))
                {
                    ulong TotalSize = 0;

                    foreach (ulong Size in SelectedItemsCopy.Cast<FileSystemStorageFile>().Select((Item) => Item.Size).ToArray())
                    {
                        TotalSize += Size;
                    }

                    SizeInfo = $"  |  {TotalSize.GetFileSizeDescription()}";
                }

                if (StatusTipsSplit.Length > 0)
                {
                    StatusTips.Text = $"{StatusTipsSplit[0]}  |  {Globalization.GetString("FilePresenterBottomStatusTip_SelectedItem").Replace("{ItemNum}", SelectedItemsCopy.Count.ToString())}{SizeInfo}";
                }
                else
                {
                    StatusTips.Text += $"  |  {Globalization.GetString("FilePresenterBottomStatusTip_SelectedItem").Replace("{ItemNum}", SelectedItemsCopy.Count.ToString())}{SizeInfo}";
                }
            }
            else
            {
                if (StatusTipsSplit.Length > 0)
                {
                    StatusTips.Text = StatusTipsSplit[0];
                }
            }

            if (SettingPage.IsQuicklookEnabled
                && e.AddedItems.Count <= 1)
            {
                using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                {
                    if (await Exclusive.Controller.CheckIfQuicklookIsAvaliableAsync())
                    {
                        string ViewPathWithQuicklook = e.AddedItems.OfType<FileSystemStorageItemBase>().FirstOrDefault()?.Path;

                        if (string.IsNullOrEmpty(ViewPathWithQuicklook))
                        {
                            ViewPathWithQuicklook = CurrentFolder?.Path;
                        }

                        if (!string.IsNullOrEmpty(ViewPathWithQuicklook))
                        {
                            await Exclusive.Controller.SwitchQuicklookAsync(ViewPathWithQuicklook);
                        }
                    }
                }
            }
        }

        private void ViewControl_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            DelayDragCancellation?.Cancel();
        }

        private void ViewControl_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.OriginalSource is FrameworkElement Element)
            {
                if (Element.DataContext is FileSystemStorageItemBase Item)
                {
                    if (Element.FindParentOfType<TextBox>() is null)
                    {
                        PointerPoint PointerInfo = e.GetCurrentPoint(null);

                        if (PointerInfo.Properties.IsMiddleButtonPressed && Item is FileSystemStorageFolder)
                        {
                            SelectionExtention.Disable();
                            SelectedItem = Item;
                            _ = TabViewContainer.Current.CreateNewTabAsync(Item.Path);
                        }
                        else if (Element.FindParentOfType<SelectorItem>() is SelectorItem SItem)
                        {
                            if (e.KeyModifiers == VirtualKeyModifiers.None && ItemPresenter.SelectionMode != ListViewSelectionMode.Multiple)
                            {
                                if (ItemPresenter.SelectedItems.Contains(Item))
                                {
                                    SelectionExtention.Disable();

                                    if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
                                    {
                                        DelayDragCancellation?.Cancel();
                                        DelayDragCancellation?.Dispose();
                                        DelayDragCancellation = new CancellationTokenSource();

                                        Task.Delay(300).ContinueWith(async (task, input) =>
                                        {
                                            try
                                            {
                                                if (input is (CancellationToken Token, UIElement Item, PointerPoint Point) && !Token.IsCancellationRequested)
                                                {
                                                    await Item.StartDragAsync(Point);
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                LogTracer.Log(ex, "Could not start drag item");
                                            }
                                        }, (DelayDragCancellation.Token, SItem, e.GetCurrentPoint(SItem)), TaskScheduler.FromCurrentSynchronizationContext());
                                    }
                                }
                                else
                                {
                                    if (PointerInfo.Properties.IsLeftButtonPressed)
                                    {
                                        SelectedItem = Item;
                                    }

                                    switch (Element)
                                    {
                                        case Grid:
                                        case ListViewItemPresenter:
                                            {
                                                SelectionExtention.Enable();
                                                break;
                                            }
                                        default:
                                            {
                                                SelectionExtention.Disable();

                                                if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
                                                {
                                                    DelayDragCancellation?.Cancel();
                                                    DelayDragCancellation?.Dispose();
                                                    DelayDragCancellation = new CancellationTokenSource();

                                                    Task.Delay(300).ContinueWith(async (task, input) =>
                                                    {
                                                        try
                                                        {
                                                            if (input is (CancellationToken Token, UIElement Item, PointerPoint Point) && !Token.IsCancellationRequested)
                                                            {
                                                                await Item.StartDragAsync(Point);
                                                            }
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            LogTracer.Log(ex, "Could not start drag item");
                                                        }
                                                    }, (DelayDragCancellation.Token, SItem, e.GetCurrentPoint(SItem)), TaskScheduler.FromCurrentSynchronizationContext());
                                                }

                                                break;
                                            }
                                    }
                                }
                            }
                            else
                            {
                                SelectionExtention.Disable();
                            }
                        }
                    }
                    else
                    {
                        SelectionExtention.Disable();
                    }
                }
                else if (Element.FindParentOfType<ScrollBar>() is ScrollBar)
                {
                    SelectionExtention.Disable();
                }
                else
                {
                    SelectedItem = null;
                    SelectionExtention.Enable();
                }
            }
            else
            {
                SelectedItem = null;
                SelectionExtention.Enable();
            }
        }

        private async void ViewControl_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (e.PointerDeviceType == PointerDeviceType.Mouse)
            {
                e.Handled = true;
                Container.BlockKeyboardShortCutInput = true;

                try
                {
                    await HandleContextActionRequested(e, e.GetPosition((FrameworkElement)sender));
                }
                catch (Exception ex)
                {
                    LogTracer.Log(ex, "Could not execute the context action");
                }

                Container.BlockKeyboardShortCutInput = false;
            }
        }

        private async Task HandleContextActionRequested(RoutedEventArgs Args, Point Position)
        {
            ContextMenuCancellation?.Cancel();
            ContextMenuCancellation?.Dispose();
            ContextMenuCancellation = new CancellationTokenSource();

            if (!SettingPage.IsDoubleClickEnabled)
            {
                DelaySelectionCancellation?.Cancel();
            }

            if (ItemPresenter is GridView)
            {
                if ((Args.OriginalSource as FrameworkElement)?.DataContext is FileSystemStorageItemBase Context)
                {
                    if (ItemPresenter.SelectedItems.Count > 1 && ItemPresenter.SelectedItems.Contains(Context))
                    {
                        await MixedFlyout.ShowCommandBarFlyoutWithExtraContextMenuItems(ItemPresenter,
                                                                                        Position,
                                                                                        ContextMenuCancellation.Token,
                                                                                        ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().Select((Item) => Item.Path).ToArray());
                    }
                    else
                    {
                        SelectedItem = Context;

                        CommandBarFlyout ContextFlyout = Context switch
                        {
                            LinkStorageFile => LinkItemFlyout,
                            FileSystemStorageFolder => FolderFlyout,
                            FileSystemStorageFile => FileFlyout,
                            _ => throw new NotImplementedException()
                        };

                        await ContextFlyout.ShowCommandBarFlyoutWithExtraContextMenuItems(ItemPresenter,
                                                                                          Position,
                                                                                          ContextMenuCancellation.Token,
                                                                                          ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().Select((Item) => Item.Path).ToArray());
                    }
                }
                else
                {
                    SelectedItem = null;
                    await EmptyFlyout.ShowCommandBarFlyoutWithExtraContextMenuItems(ItemPresenter,
                                                                                    Position,
                                                                                    ContextMenuCancellation.Token,
                                                                                    CurrentFolder.Path);
                }
            }
            else
            {
                if (Args.OriginalSource is FrameworkElement Element)
                {
                    if (Element.DataContext is FileSystemStorageItemBase Context)
                    {
                        if (ItemPresenter.SelectedItems.Count > 1 && ItemPresenter.SelectedItems.Contains(Context))
                        {
                            await MixedFlyout.ShowCommandBarFlyoutWithExtraContextMenuItems(ItemPresenter,
                                                                                            Position,
                                                                                            ContextMenuCancellation.Token,
                                                                                            ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().Select((Item) => Item.Path).ToArray());
                        }
                        else
                        {
                            if (SelectedItem == Context)
                            {
                                CommandBarFlyout ContextFlyout = Context switch
                                {
                                    LinkStorageFile => LinkItemFlyout,
                                    FileSystemStorageFolder => FolderFlyout,
                                    FileSystemStorageFile => FileFlyout,
                                    _ => throw new NotImplementedException()
                                };

                                await ContextFlyout.ShowCommandBarFlyoutWithExtraContextMenuItems(ItemPresenter,
                                                                                                  Position,
                                                                                                  ContextMenuCancellation.Token,
                                                                                                  ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().Select((Item) => Item.Path).ToArray());
                            }
                            else
                            {
                                if (Args.OriginalSource is TextBlock)
                                {
                                    SelectedItem = Context;

                                    CommandBarFlyout ContextFlyout = Context switch
                                    {
                                        LinkStorageFile => LinkItemFlyout,
                                        FileSystemStorageFolder => FolderFlyout,
                                        FileSystemStorageFile => FileFlyout,
                                        _ => throw new NotImplementedException()
                                    };

                                    await ContextFlyout.ShowCommandBarFlyoutWithExtraContextMenuItems(ItemPresenter,
                                                                                                      Position,
                                                                                                      ContextMenuCancellation.Token,
                                                                                                      ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().Select((Item) => Item.Path).ToArray());
                                }
                                else
                                {
                                    SelectedItem = null;
                                    await EmptyFlyout.ShowCommandBarFlyoutWithExtraContextMenuItems(ItemPresenter,
                                                                                                    Position,
                                                                                                    ContextMenuCancellation.Token,
                                                                                                    CurrentFolder.Path);
                                }
                            }
                        }
                    }
                    else
                    {
                        SelectedItem = null;
                        await EmptyFlyout.ShowCommandBarFlyoutWithExtraContextMenuItems(ItemPresenter,
                                                                                        Position,
                                                                                        ContextMenuCancellation.Token,
                                                                                        CurrentFolder.Path);
                    }
                }
            }
        }

        private async void FileProperty_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem is FileSystemStorageFile File)
            {
                PropertiesWindowBase NewWindow = await PropertiesWindowBase.CreateAsync(File);
                await NewWindow.ShowAsync(new Point(Window.Current.Bounds.Width / 2 - 200, Window.Current.Bounds.Height / 2 - 300));
            }
        }

        private async void Compression_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem is FileSystemStorageFile File)
            {
                CompressDialog Dialog = new CompressDialog(File);

                if ((await Dialog.ShowAsync()) == ContentDialogResult.Primary)
                {
                    QueueTaskController.EnqueueCompressionOpeartion(Dialog.Type, Dialog.Algorithm, Dialog.Level, File.Path, Path.Combine(CurrentFolder.Path, Dialog.FileName));
                }
            }
        }

        private void Decompression_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem is FileSystemStorageFile File)
            {
                if (File.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    || File.Name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
                    || File.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                    || File.Name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
                    || File.Name.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase)
                    || File.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                    || File.Name.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase)
                    || File.Name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))

                {
                    QueueTaskController.EnqueueDecompressionOpeartion(File.Path, CurrentFolder.Path, (sender as FrameworkElement)?.Name == "DecompressionOption2");
                }
            }
        }

        private async void ViewControl_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;

            DelayRenameCancellation?.Cancel();

            if ((e.OriginalSource as FrameworkElement)?.DataContext is FileSystemStorageItemBase Item)
            {
                CoreWindow CWindow = CoreWindow.GetForCurrentThread();

                if (CWindow.GetKeyState(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down))
                {
                    PropertiesWindowBase NewWindow = await PropertiesWindowBase.CreateAsync(Item);
                    await NewWindow.ShowAsync(new Point(Window.Current.Bounds.Width / 2 - 200, Window.Current.Bounds.Height / 2 - 300));
                }
                else if (CWindow.GetKeyState(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down) && Item is FileSystemStorageFolder)
                {
                    await TabViewContainer.Current.CreateNewTabAsync(Item.Path);
                }
                else if (ItemPresenter.SelectionMode != ListViewSelectionMode.Multiple)
                {
                    await EnterSelectedItemAsync(Item).ConfigureAwait(false);
                }
            }
            else if (e.OriginalSource is Grid)
            {
                if (Path.GetPathRoot(CurrentFolder?.Path).Equals(CurrentFolder?.Path, StringComparison.OrdinalIgnoreCase))
                {
                    await DisplayItemsInFolder(RootStorageFolder.Instance);
                }
                else if (Container.GoParentFolder.IsEnabled)
                {
                    Container.GoParentFolder_Click(null, null);
                }
            }
        }

        private async void Transcode_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (!await FileSystemStorageItemBase.CheckExistAsync(SelectedItem.Path))
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync();

                return;
            }

            if (GeneralTransformer.IsAnyTransformTaskRunning)
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_TipTitle"),
                    Content = Globalization.GetString("QueueDialog_TaskWorking_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };
                _ = await Dialog.ShowAsync();

                return;
            }

            switch (SelectedItem.Type.ToLower())
            {
                case ".mkv":
                case ".mp4":
                case ".mp3":
                case ".flac":
                case ".wma":
                case ".wmv":
                case ".m4a":
                case ".mov":
                case ".alac":
                    {
                        if ((await SelectedItem.GetStorageItemAsync()) is StorageFile Source)
                        {
                            TranscodeDialog dialog = new TranscodeDialog(Source);

                            if ((await dialog.ShowAsync()) == ContentDialogResult.Primary)
                            {
                                try
                                {
                                    string DestFilePath = Path.Combine(CurrentFolder.Path, $"{Path.GetFileNameWithoutExtension(Source.Path)}.{dialog.MediaTranscodeEncodingProfile.ToLower()}");

                                    if (await FileSystemStorageItemBase.CreateNewAsync(DestFilePath, StorageItemTypes.File, CreateOption.GenerateUniqueName) is FileSystemStorageItemBase Item)
                                    {
                                        if (await Item.GetStorageItemAsync() is StorageFile DestinationFile)
                                        {
                                            await GeneralTransformer.TranscodeFromAudioOrVideoAsync(Source, DestinationFile, dialog.MediaTranscodeEncodingProfile, dialog.MediaTranscodeQuality, dialog.SpeedUp);
                                        }
                                        else
                                        {
                                            throw new FileNotFoundException();
                                        }
                                    }
                                    else
                                    {
                                        throw new FileNotFoundException();
                                    }
                                }
                                catch (Exception)
                                {
                                    QueueContentDialog Dialog = new QueueContentDialog
                                    {
                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                        Content = Globalization.GetString("QueueDialog_UnauthorizedCreateNewFile_Content"),
                                        PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                                        CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                                    };

                                    await Dialog.ShowAsync();
                                }
                            }
                        }
                        else
                        {
                            QueueContentDialog Dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_UnableAccessFile_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                            };

                            _ = await Dialog.ShowAsync();
                        }

                        break;
                    }
                case ".png":
                case ".bmp":
                case ".jpg":
                case ".heic":
                case ".tiff":
                    {
                        if (SelectedItem is FileSystemStorageFile File)
                        {
                            TranscodeImageDialog Dialog = null;

                            using (IRandomAccessStream OriginStream = await File.GetRandomAccessStreamFromFileAsync(AccessMode.Read))
                            {
                                BitmapDecoder Decoder = await BitmapDecoder.CreateAsync(OriginStream);
                                Dialog = new TranscodeImageDialog(Decoder.PixelWidth, Decoder.PixelHeight);
                            }

                            if (await Dialog.ShowAsync() == ContentDialogResult.Primary)
                            {
                                await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Transcoding"));

                                await GeneralTransformer.TranscodeFromImageAsync(File, Dialog.TargetFile, Dialog.IsEnableScale, Dialog.ScaleWidth, Dialog.ScaleHeight, Dialog.InterpolationMode);

                                await Container.LoadingActivation(false);
                            }
                        }

                        break;
                    }
            }
        }

        private async void FolderProperty_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem is FileSystemStorageFolder Folder)
            {
                PropertiesWindowBase NewWindow = await PropertiesWindowBase.CreateAsync(Folder);
                await NewWindow.ShowAsync(new Point(Window.Current.Bounds.Width / 2 - 200, Window.Current.Bounds.Height / 2 - 300));
            }
        }

        private async void WIFIShare_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem != null)
            {
                if (QRTeachTip.IsOpen)
                {
                    QRTeachTip.IsOpen = false;
                }

                await Task.Run(() =>
                {
                    SpinWait.SpinUntil(() => WiFiProvider == null);
                });

                WiFiProvider = new WiFiShareProvider();
                WiFiProvider.ThreadExitedUnexpectly += WiFiProvider_ThreadExitedUnexpectly;

                using (MD5 MD5Alg = MD5.Create())
                {
                    string Hash = MD5Alg.GetHash(SelectedItem.Path);
                    QRText.Text = WiFiProvider.CurrentUri + Hash;
                    WiFiProvider.FilePathMap = new KeyValuePair<string, string>(Hash, SelectedItem.Path);
                }

                QrCodeEncodingOptions options = new QrCodeEncodingOptions()
                {
                    DisableECI = true,
                    CharacterSet = "UTF-8",
                    Width = 250,
                    Height = 250,
                    ErrorCorrection = ErrorCorrectionLevel.Q
                };

                BarcodeWriter Writer = new BarcodeWriter
                {
                    Format = BarcodeFormat.QR_CODE,
                    Options = options
                };

                WriteableBitmap Bitmap = Writer.Write(QRText.Text);
                using (SoftwareBitmap PreTransImage = SoftwareBitmap.CreateCopyFromBuffer(Bitmap.PixelBuffer, BitmapPixelFormat.Bgra8, 250, 250))
                using (SoftwareBitmap TransferImage = ComputerVisionProvider.ExtendImageBorder(PreTransImage, Colors.White, 0, 75, 75, 0))
                {
                    SoftwareBitmapSource Source = new SoftwareBitmapSource();
                    QRImage.Source = Source;
                    await Source.SetBitmapAsync(TransferImage);
                }

                await Task.Delay(500);

                QRTeachTip.Target = ItemPresenter.ContainerFromItem(SelectedItem) as FrameworkElement;
                QRTeachTip.IsOpen = true;

                await WiFiProvider.StartToListenRequest().ConfigureAwait(false);
            }
        }

        private async void WiFiProvider_ThreadExitedUnexpectly(object sender, Exception e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                QRTeachTip.IsOpen = false;

                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_WiFiError_Content") + e.Message,
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };
                _ = await dialog.ShowAsync();
            });
        }

        private void CopyLinkButton_Click(object sender, RoutedEventArgs e)
        {
            DataPackage Package = new DataPackage();
            Package.SetText(QRText.Text);
            Clipboard.SetContent(Package);
        }

        private async void UseSystemFileMananger_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            _ = await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
        }

        private async void ParentProperty_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (await FileSystemStorageItemBase.CheckExistAsync(CurrentFolder.Path))
            {
                if (CurrentFolder.Path.Equals(Path.GetPathRoot(CurrentFolder.Path), StringComparison.OrdinalIgnoreCase)
                    && CommonAccessCollection.DriveList.FirstOrDefault((Drive) => Drive.Path.Equals(CurrentFolder.Path, StringComparison.OrdinalIgnoreCase)) is DriveDataBase Drive)
                {
                    await new DriveInfoDialog(Drive).ShowAsync();
                }
                else
                {
                    PropertiesWindowBase NewWindow = await PropertiesWindowBase.CreateAsync(CurrentFolder);
                    NewWindow.HandleRenameAutomatically = false;
                    NewWindow.RenameRequested += Content_RenameRequested;

                    async void Content_RenameRequested(object sender, FileRenamedDeferredEventArgs e)
                    {
                        EventDeferral Deferral = e.GetDeferral();

                        try
                        {
                            if (await FileSystemStorageItemBase.OpenAsync(e.Path) is FileSystemStorageFolder Folder)
                            {
                                string NewName = await Folder.RenameAsync(e.NewName);
                                string NewPath = Path.Combine(Path.GetDirectoryName(e.Path), NewName);

                                if (!await DisplayItemsInFolder(NewPath, SkipNavigationRecord: true))
                                {
                                    QueueContentDialog dialog = new QueueContentDialog
                                    {
                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                        Content = $"{Globalization.GetString("QueueDialog_LocatePathFailure_Content")} {Environment.NewLine}\"{NewPath}\"",
                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton"),
                                    };

                                    await dialog.ShowAsync();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogTracer.Log(ex, $"Could not rename the item. Path: \"{e.Path}\"");
                        }
                        finally
                        {
                            Deferral.Complete();
                        }
                    }

                    NewWindow.WindowClosed += NewWindow_WindowClosed;

                    void NewWindow_WindowClosed(object sender, EventArgs e)
                    {
                        NewWindow.WindowClosed -= NewWindow_WindowClosed;
                        NewWindow.RenameRequested -= Content_RenameRequested;
                    }

                    await NewWindow.ShowAsync(new Point(Window.Current.Bounds.Width / 2 - 200, Window.Current.Bounds.Height / 2 - 300));
                }
            }
            else
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                await Dialog.ShowAsync();
            }
        }

        private async void ItemOpen_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem is FileSystemStorageItemBase ReFile)
            {
                await EnterSelectedItemAsync(ReFile).ConfigureAwait(false);
            }
        }

        private void QRText_GotFocus(object sender, RoutedEventArgs e)
        {
            ((TextBox)sender).SelectAll();
        }

        private async void CreateFolder_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (await FileSystemStorageItemBase.CheckExistAsync(CurrentFolder.Path))
            {
                if (await FileSystemStorageItemBase.CreateNewAsync(Path.Combine(CurrentFolder.Path, Globalization.GetString("Create_NewFolder_Admin_Name")), StorageItemTypes.Folder, CreateOption.GenerateUniqueName) is FileSystemStorageItemBase NewFolder)
                {
                    OperationRecorder.Current.Push(new string[] { $"{NewFolder.Path}||New" });

                    FileSystemStorageItemBase TargetItem = null;

                    for (int MaxSearchLimit = 0; MaxSearchLimit < 4; MaxSearchLimit++)
                    {
                        TargetItem = FileCollection.FirstOrDefault((Item) => Item == NewFolder);

                        if (TargetItem == null)
                        {
                            await Task.Delay(500);
                        }
                        else
                        {
                            SelectedItem = TargetItem;
                            ItemPresenter.ScrollIntoView(TargetItem);

                            if ((ItemPresenter.ContainerFromItem(TargetItem) as SelectorItem)?.ContentTemplateRoot is FrameworkElement Element)
                            {
                                if (Element.FindName("NameLabel") is TextBlock NameLabel)
                                {
                                    NameLabel.Visibility = Visibility.Collapsed;
                                }

                                if (Element.FindName("NameEditBox") is TextBox EditBox)
                                {
                                    EditBox.BeforeTextChanging += EditBox_BeforeTextChanging;
                                    EditBox.PreviewKeyDown += EditBox_PreviewKeyDown;
                                    EditBox.LostFocus += EditBox_LostFocus;
                                    EditBox.Text = TargetItem.Name;
                                    EditBox.Visibility = Visibility.Visible;
                                    EditBox.Focus(FocusState.Programmatic);
                                    EditBox.SelectAll();
                                }

                                Container.BlockKeyboardShortCutInput = true;
                            }

                            break;
                        }
                    }
                }
                else
                {
                    QueueContentDialog dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnauthorizedCreateFolder_Content"),
                        PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                    };

                    await dialog.ShowAsync();
                }
            }
            else
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync();
            }
        }

        private async void EmptyFlyout_Opening(object sender, object e)
        {
            if (SettingPage.IsDetachTreeViewAndPresenter)
            {
                ExpandToCurrentFolder.Visibility = Visibility.Collapsed;
            }
            else
            {
                ExpandToCurrentFolder.Visibility = Visibility.Visible;
            }

            try
            {
                DataPackageView Package = Clipboard.GetContent();

                if (await Package.CheckIfContainsAvailableDataAsync())
                {
                    Paste.IsEnabled = true;
                }
                else
                {
                    Paste.IsEnabled = false;
                }
            }
            catch
            {
                Paste.IsEnabled = false;
            }

            if (OperationRecorder.Current.IsNotEmpty)
            {
                Undo.IsEnabled = true;
            }
            else
            {
                Undo.IsEnabled = false;
            }
        }

        private async void SystemShare_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem != null)
            {
                if (!await FileSystemStorageItemBase.CheckExistAsync(SelectedItem.Path))
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync();
                }
                else
                {
                    if ((await SelectedItem.GetStorageItemAsync()) is StorageFile ShareItem)
                    {
                        DataTransferManager.GetForCurrentView().DataRequested += (s, args) =>
                        {
                            DataPackage Package = new DataPackage();
                            Package.Properties.Title = ShareItem.DisplayName;
                            Package.Properties.Description = ShareItem.DisplayType;
                            Package.SetStorageItems(new StorageFile[] { ShareItem });
                            args.Request.Data = Package;
                        };

                        DataTransferManager.ShowShareUI();
                    }
                    else
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_UnableAccessFile_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        _ = await Dialog.ShowAsync();
                    }
                }
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            try
            {
                if (!await DisplayItemsInFolder(CurrentFolder.Path, true))
                {
                    QueueContentDialog dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = $"{Globalization.GetString("QueueDialog_LocatePathFailure_Content")} {Environment.NewLine}\"{CurrentFolder.Path}\"",
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton"),
                    };

                    await dialog.ShowAsync();
                }
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex, $"{ nameof(Refresh_Click)} throw an exception");
            }
        }

        private async void ViewControl_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!SettingPage.IsDoubleClickEnabled
                && ItemPresenter.SelectionMode != ListViewSelectionMode.Multiple
                && e.ClickedItem is FileSystemStorageItemBase ReFile)
            {
                DelaySelectionCancellation?.Cancel();

                CoreVirtualKeyStates CtrlState = Window.Current.CoreWindow.GetKeyState(VirtualKey.Control);
                CoreVirtualKeyStates ShiftState = Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift);

                if (!CtrlState.HasFlag(CoreVirtualKeyStates.Down) && !ShiftState.HasFlag(CoreVirtualKeyStates.Down))
                {
                    await EnterSelectedItemAsync(ReFile);
                }
            }
        }

        public async Task EnterSelectedItemAsync(string Path, bool RunAsAdministrator = false)
        {
            if (RootStorageFolder.Instance.Path.Equals(Path, StringComparison.OrdinalIgnoreCase))
            {
                await EnterSelectedItemAsync(RootStorageFolder.Instance, RunAsAdministrator);
            }
            else if (await FileSystemStorageItemBase.OpenAsync(Path) is FileSystemStorageItemBase Item)
            {
                await EnterSelectedItemAsync(Item, RunAsAdministrator);
            }
        }

        public async Task EnterSelectedItemAsync(FileSystemStorageItemBase ReFile, bool RunAsAdministrator = false)
        {
            if (Interlocked.Exchange(ref TabTarget, ReFile) == null)
            {
                try
                {
                    switch (TabTarget)
                    {
                        case FileSystemStorageFile File:
                            {
                                if (!await FileSystemStorageItemBase.CheckExistAsync(File.Path))
                                {
                                    QueueContentDialog Dialog = new QueueContentDialog
                                    {
                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                        Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                    };

                                    _ = await Dialog.ShowAsync();

                                    return;
                                }

                                switch (File.Type.ToLower())
                                {
                                    case ".exe":
                                    case ".bat":
                                    case ".msi":
                                        {
                                            using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                                            {
                                                if (!await Exclusive.Controller.RunAsync(File.Path, Path.GetDirectoryName(File.Path), WindowState.Normal, RunAsAdministrator))
                                                {
                                                    QueueContentDialog Dialog = new QueueContentDialog
                                                    {
                                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                        Content = Globalization.GetString("QueueDialog_LaunchFailed_Content"),
                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                    };

                                                    await Dialog.ShowAsync();
                                                }
                                            }

                                            break;
                                        }
                                    case ".msc":
                                        {
                                            using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                                            {
                                                if (!await Exclusive.Controller.RunAsync("powershell.exe", string.Empty, WindowState.Normal, false, true, false, "-Command", File.Path))
                                                {
                                                    QueueContentDialog Dialog = new QueueContentDialog
                                                    {
                                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                        Content = Globalization.GetString("QueueDialog_LaunchFailed_Content"),
                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                    };

                                                    await Dialog.ShowAsync();
                                                }
                                            }

                                            break;
                                        }
                                    case ".lnk":
                                        {
                                            if (File is LinkStorageFile Item)
                                            {
                                                if (Item.LinkType == ShellLinkType.Normal)
                                                {
                                                    switch (await FileSystemStorageItemBase.OpenAsync(Item.LinkTargetPath))
                                                    {
                                                        case FileSystemStorageFolder:
                                                            {
                                                                if (!await DisplayItemsInFolder(Item.LinkTargetPath))
                                                                {
                                                                    QueueContentDialog dialog = new QueueContentDialog
                                                                    {
                                                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                                        Content = $"{Globalization.GetString("QueueDialog_LocatePathFailure_Content")} {Environment.NewLine}\"{Item.LinkTargetPath}\"",
                                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton"),
                                                                    };

                                                                    await dialog.ShowAsync();
                                                                }

                                                                break;
                                                            }
                                                        case FileSystemStorageFile:
                                                            {
                                                                if (!await Item.LaunchAsync())
                                                                {
                                                                    QueueContentDialog Dialog = new QueueContentDialog
                                                                    {
                                                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                                        Content = Globalization.GetString("QueueDialog_LaunchFailed_Content"),
                                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                                    };

                                                                    await Dialog.ShowAsync();
                                                                }
                                                                break;
                                                            }
                                                    }
                                                }
                                                else
                                                {
                                                    if (!await Item.LaunchAsync())
                                                    {
                                                        QueueContentDialog Dialog = new QueueContentDialog
                                                        {
                                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                            Content = Globalization.GetString("QueueDialog_LaunchFailed_Content"),
                                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                        };

                                                        await Dialog.ShowAsync();
                                                    }
                                                }
                                            }

                                            break;
                                        }
                                    case ".url":
                                        {
                                            if (File is UrlStorageFile Item)
                                            {
                                                if (!await Item.LaunchAsync())
                                                {
                                                    QueueContentDialog Dialog = new QueueContentDialog
                                                    {
                                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                        Content = Globalization.GetString("QueueDialog_LaunchFailed_Content"),
                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                    };

                                                    await Dialog.ShowAsync();
                                                }
                                            }

                                            break;
                                        }
                                    default:
                                        {
                                            string AdminExecutablePath = SQLite.Current.GetDefaultProgramPickerRecord(File.Type);

                                            if (string.IsNullOrEmpty(AdminExecutablePath) || AdminExecutablePath == Package.Current.Id.FamilyName)
                                            {
                                                if (!TryOpenInternally(File))
                                                {
                                                    if (await File.GetStorageItemAsync() is StorageFile SFile)
                                                    {
                                                        if (!await Launcher.LaunchFileAsync(SFile))
                                                        {
                                                            using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                                                            {
                                                                if (!await Exclusive.Controller.RunAsync(File.Path))
                                                                {
                                                                    QueueContentDialog Dialog = new QueueContentDialog
                                                                    {
                                                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                                        Content = Globalization.GetString("QueueDialog_LaunchFailed_Content"),
                                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                                    };

                                                                    await Dialog.ShowAsync();
                                                                }
                                                            }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                                                        {
                                                            if (!await Exclusive.Controller.RunAsync(File.Path))
                                                            {
                                                                QueueContentDialog Dialog = new QueueContentDialog
                                                                {
                                                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                                    Content = Globalization.GetString("QueueDialog_LaunchFailed_Content"),
                                                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                                };

                                                                await Dialog.ShowAsync();
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                if (Path.IsPathRooted(AdminExecutablePath))
                                                {
                                                    using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                                                    {
                                                        if (!await Exclusive.Controller.RunAsync(AdminExecutablePath, Path.GetDirectoryName(AdminExecutablePath), Parameters: File.Path))
                                                        {
                                                            QueueContentDialog Dialog = new QueueContentDialog
                                                            {
                                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                                Content = Globalization.GetString("QueueDialog_LaunchFailed_Content"),
                                                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                            };

                                                            await Dialog.ShowAsync();
                                                        }
                                                    }
                                                }
                                                else
                                                {
                                                    if ((await Launcher.FindFileHandlersAsync(File.Type)).FirstOrDefault((Item) => Item.PackageFamilyName == AdminExecutablePath) is AppInfo Info)
                                                    {
                                                        if (await File.GetStorageItemAsync() is StorageFile InnerFile)
                                                        {
                                                            if (!await Launcher.LaunchFileAsync(InnerFile, new LauncherOptions { TargetApplicationPackageFamilyName = Info.PackageFamilyName, DisplayApplicationPicker = false }))
                                                            {
                                                                using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                                                                {
                                                                    if (!await Exclusive.Controller.LaunchUWPFromAUMIDAsync(Info.AppUserModelId, File.Path))
                                                                    {
                                                                        LogTracer.Log("Launch UWP failed and fall back to open ProgramPickerDialog");

                                                                        await OpenFileWithProgramPicker(File);
                                                                    }
                                                                }
                                                            }
                                                        }
                                                        else
                                                        {
                                                            using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                                                            {
                                                                if (!await Exclusive.Controller.LaunchUWPFromAUMIDAsync(Info.AppUserModelId, File.Path))
                                                                {
                                                                    QueueContentDialog Dialog = new QueueContentDialog
                                                                    {
                                                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                                        Content = Globalization.GetString("QueueDialog_UnableAccessFile_Content"),
                                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                                    };

                                                                    await Dialog.ShowAsync();
                                                                }
                                                            }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        await OpenFileWithProgramPicker(File);
                                                    }
                                                }
                                            }

                                            break;
                                        }
                                }

                                break;
                            }
                        case FileSystemStorageFolder Folder:
                            {
                                if (!await DisplayItemsInFolder(Folder))
                                {
                                    QueueContentDialog Dialog = new QueueContentDialog
                                    {
                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                        Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton"),
                                    };

                                    await Dialog.ShowAsync();
                                }

                                break;
                            }
                    }
                }
                catch (Exception ex)
                {
                    LogTracer.Log(ex, $"{nameof(EnterSelectedItemAsync)} throw an exception");
                }
                finally
                {
                    Interlocked.Exchange(ref TabTarget, null);
                }
            }
        }

        private bool TryOpenInternally(FileSystemStorageFile File)
        {
            Type InternalType = File.Type.ToLower() switch
            {
                ".jpg" or ".png" or ".bmp" => typeof(PhotoViewer),
                ".mkv" or ".mp4" or ".mp3" or
                ".flac" or ".wma" or ".wmv" or
                ".m4a" or ".mov" or ".alac" => typeof(MediaPlayer),
                ".txt" => typeof(TextViewer),
                ".pdf" => typeof(PdfReader),
                ".zip" => typeof(CompressionViewer),
                _ => null
            };


            if (InternalType != null)
            {
                NavigationTransitionInfo NavigationTransition = AnimationController.Current.IsEnableAnimation
                                                ? new DrillInNavigationTransitionInfo()
                                                : new SuppressNavigationTransitionInfo();

                Container.Frame.Navigate(InternalType, File, NavigationTransition);

                return true;
            }
            else
            {
                return false;
            }
        }

        private async Task OpenFileWithProgramPicker(FileSystemStorageFile File)
        {
            ProgramPickerDialog Dialog = new ProgramPickerDialog(File);

            if (await Dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (Dialog.UserPickedItem == ProgramPickerItem.InnerViewer)
                {
                    if (!TryOpenInternally(File))
                    {
                        QueueContentDialog Dialog1 = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_LaunchFailed_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        await Dialog1.ShowAsync();
                    }
                }
                else
                {
                    if (!await Dialog.UserPickedItem.LaunchAsync(File.Path))
                    {
                        QueueContentDialog Dialog1 = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_LaunchFailed_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        await Dialog1.ShowAsync();
                    }
                }
            }
        }

        private async void VideoEdit_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (GeneralTransformer.IsAnyTransformTaskRunning)
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_TipTitle"),
                    Content = Globalization.GetString("QueueDialog_TaskWorking_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync();
            }
            else
            {
                if ((await SelectedItem.GetStorageItemAsync()) is StorageFile File)
                {
                    VideoEditDialog Dialog = new VideoEditDialog(File);

                    if ((await Dialog.ShowAsync()) == ContentDialogResult.Primary)
                    {
                        if (await CurrentFolder.GetStorageItemAsync() is StorageFolder Folder)
                        {
                            StorageFile ExportFile = await Folder.CreateFileAsync($"{File.DisplayName} - {Globalization.GetString("Crop_Image_Name_Tail")}{Dialog.ExportFileType}", CreationCollisionOption.GenerateUniqueName);

                            await GeneralTransformer.GenerateCroppedVideoFromOriginAsync(ExportFile, Dialog.Composition, Dialog.MediaEncoding, Dialog.TrimmingPreference);
                        }
                    }
                }
                else
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnableAccessFile_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync();
                }
            }
        }

        private async void VideoMerge_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (GeneralTransformer.IsAnyTransformTaskRunning)
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_TipTitle"),
                    Content = Globalization.GetString("QueueDialog_TaskWorking_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync();

                return;
            }

            if ((await SelectedItem.GetStorageItemAsync()) is StorageFile Item)
            {
                VideoMergeDialog Dialog = new VideoMergeDialog(Item);

                if ((await Dialog.ShowAsync()) == ContentDialogResult.Primary)
                {
                    if (await CurrentFolder.GetStorageItemAsync() is StorageFolder Folder)
                    {
                        StorageFile ExportFile = await Folder.CreateFileAsync($"{Item.DisplayName} - {Globalization.GetString("Merge_Image_Name_Tail")}{Dialog.ExportFileType}", CreationCollisionOption.GenerateUniqueName);

                        await GeneralTransformer.GenerateMergeVideoFromOriginAsync(ExportFile, Dialog.Composition, Dialog.MediaEncoding);
                    }
                }
            }
        }

        private async void ChooseOtherApp_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem is FileSystemStorageFile File)
            {
                await OpenFileWithProgramPicker(File);
            }
        }

        private async void RunWithSystemAuthority_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem != null)
            {
                await EnterSelectedItemAsync(SelectedItem, true).ConfigureAwait(false);
            }
        }

        private void ListHeader_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button Btn)
            {
                SortTarget STarget = Btn.Name switch
                {
                    "ListHeaderName" => SortTarget.Name,
                    "ListHeaderModifiedTime" => SortTarget.ModifiedTime,
                    "ListHeaderType" => SortTarget.Type,
                    "ListHeaderSize" => SortTarget.Size,
                    _ => throw new NotSupportedException()
                };

                PathConfiguration Config = SQLite.Current.GetPathConfiguration(CurrentFolder.Path);

                if (Config.SortTarget == STarget)
                {
                    if (Config.SortDirection == SortDirection.Ascending)
                    {
                        SortCollectionGenerator.SaveSortConfigOnPath(CurrentFolder.Path, STarget, SortDirection.Descending);
                    }
                    else
                    {
                        SortCollectionGenerator.SaveSortConfigOnPath(CurrentFolder.Path, STarget, SortDirection.Ascending);
                    }
                }
                else
                {
                    SortCollectionGenerator.SaveSortConfigOnPath(CurrentFolder.Path, STarget, SortDirection.Ascending);
                }
            }
        }

        private void QRTeachTip_Closing(TeachingTip sender, TeachingTipClosingEventArgs args)
        {
            QRImage.Source = null;
            WiFiProvider.Dispose();
            WiFiProvider = null;
        }

        private async void CreateFile_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (sender is MenuFlyoutItem Item)
            {
                try
                {
                    string NewFileName = Globalization.GetString("NewFile_Admin_Name") + Item.Name switch
                    {
                        "TxtItem" => ".txt",
                        "CompressItem" => ".zip",
                        "RtfItem" => ".rtf",
                        "LinkItem" => ".lnk",
                        "DocItem" => ".docx",
                        "PPTItem" => ".pptx",
                        "XLSItem" => ".xlsx",
                        "BmpItem" => ".bmp",
                        _ => throw new NotSupportedException()
                    };

                    FileSystemStorageItemBase NewFile = null;

                    switch (Path.GetExtension(NewFileName).ToLower())
                    {
                        case ".zip":
                            {
                                NewFile = await SpecialTypeGenerator.Current.CreateZipFile(CurrentFolder.Path, NewFileName);
                                break;
                            }
                        case ".rtf":
                            {
                                NewFile = await SpecialTypeGenerator.Current.CreateRtfFile(CurrentFolder.Path, NewFileName);
                                break;
                            }
                        case ".xlsx":
                            {
                                NewFile = await SpecialTypeGenerator.Current.CreateExcelFile(CurrentFolder.Path, NewFileName);
                                break;
                            }
                        case ".lnk":
                            {
                                LinkOptionsDialog dialog = new LinkOptionsDialog();

                                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                                {
                                    using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                                    {
                                        if (!await Exclusive.Controller.CreateLinkAsync(new LinkDataPackage
                                        {
                                            LinkPath = Path.Combine(CurrentFolder.Path, NewFileName),
                                            LinkTargetPath = dialog.Path,
                                            WorkDirectory = dialog.WorkDirectory,
                                            WindowState = dialog.WindowState,
                                            HotKey = dialog.HotKey,
                                            Comment = dialog.Comment,
                                            Arguments = dialog.Arguments
                                        }))
                                        {
                                            throw new UnauthorizedAccessException();
                                        }
                                    }
                                }

                                return;
                            }
                        default:
                            {
                                if (await FileSystemStorageItemBase.CreateNewAsync(Path.Combine(CurrentFolder.Path, NewFileName), StorageItemTypes.File, CreateOption.GenerateUniqueName) is FileSystemStorageFile File)
                                {
                                    NewFile = File;
                                }

                                break;
                            }
                    }

                    if (NewFile != null)
                    {
                        OperationRecorder.Current.Push(new string[] { $"{NewFile.Path}||New" });

                        FileSystemStorageItemBase TargetItem = null;

                        for (int MaxSearchLimit = 0; MaxSearchLimit < 4; MaxSearchLimit++)
                        {
                            TargetItem = FileCollection.FirstOrDefault((Item) => Item == NewFile);

                            if (TargetItem == null)
                            {
                                await Task.Delay(500);
                            }
                            else
                            {
                                SelectedItem = TargetItem;
                                ItemPresenter.ScrollIntoView(TargetItem);

                                if ((ItemPresenter.ContainerFromItem(TargetItem) as SelectorItem)?.ContentTemplateRoot is FrameworkElement Element)
                                {
                                    if (Element.FindName("NameLabel") is TextBlock NameLabel)
                                    {
                                        NameLabel.Visibility = Visibility.Collapsed;
                                    }

                                    if (Element.FindName("NameEditBox") is TextBox EditBox)
                                    {
                                        EditBox.BeforeTextChanging += EditBox_BeforeTextChanging;
                                        EditBox.PreviewKeyDown += EditBox_PreviewKeyDown;
                                        EditBox.LostFocus += EditBox_LostFocus;
                                        EditBox.Text = TargetItem.Name;
                                        EditBox.Visibility = Visibility.Visible;
                                        EditBox.Focus(FocusState.Programmatic);
                                        EditBox.SelectAll();
                                    }

                                    Container.BlockKeyboardShortCutInput = true;
                                }

                                break;
                            }
                        }
                    }
                    else
                    {
                        throw new UnauthorizedAccessException();
                    }
                }
                catch (Exception ex)
                {
                    LogTracer.Log(ex, "Could not create a new file as expected");

                    await new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnauthorizedCreateNewFile_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    }.ShowAsync();
                }
            }
        }

        private async void CompressFolder_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem is FileSystemStorageFolder Folder)
            {
                if (!await FileSystemStorageItemBase.CheckExistAsync(Folder.Path))
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync();

                    return;
                }

                CompressDialog dialog = new CompressDialog(Folder);

                if ((await dialog.ShowAsync()) == ContentDialogResult.Primary)
                {
                    QueueTaskController.EnqueueCompressionOpeartion(dialog.Type, dialog.Algorithm, dialog.Level, Folder.Path, Path.Combine(CurrentFolder.Path, dialog.FileName));
                }
            }
        }

        private async void ViewControl_DragOver(object sender, DragEventArgs e)
        {
            DragOperationDeferral Deferral = e.GetDeferral();

            try
            {
                e.Handled = true;

                if (Container.BladeViewer.FindChildOfType<ScrollViewer>() is ScrollViewer Viewer)
                {
                    double XOffset = e.GetPosition(Container.BladeViewer).X;
                    double ScrollThreshold = Math.Min((Viewer.ActualWidth - 200) / 2, 100);
                    double HorizontalRightScrollThreshold = Viewer.ActualWidth - ScrollThreshold;
                    double HorizontalLeftScrollThreshold = ScrollThreshold;

                    if (XOffset > HorizontalRightScrollThreshold)
                    {
                        Viewer.ChangeView(Viewer.HorizontalOffset + XOffset - HorizontalRightScrollThreshold, null, null, false);
                    }
                    else if (XOffset < HorizontalLeftScrollThreshold)
                    {
                        Viewer.ChangeView(Viewer.HorizontalOffset + XOffset - HorizontalLeftScrollThreshold, null, null, false);
                    }
                }

                Container.CurrentPresenter = this;

                if (await e.DataView.CheckIfContainsAvailableDataAsync())
                {
                    if (e.Modifiers.HasFlag(DragDropModifiers.Control))
                    {
                        e.AcceptedOperation = DataPackageOperation.Copy;
                        e.DragUIOverride.Caption = $"{Globalization.GetString("Drag_Tip_CopyTo")} \"{CurrentFolder.Name}\"";
                    }
                    else
                    {
                        e.AcceptedOperation = DataPackageOperation.Move;
                        e.DragUIOverride.Caption = $"{Globalization.GetString("Drag_Tip_MoveTo")} \"{CurrentFolder.Name}\"";
                    }

                    e.DragUIOverride.IsContentVisible = true;
                    e.DragUIOverride.IsCaptionVisible = true;
                    e.DragUIOverride.IsGlyphVisible = true;
                }
                else
                {
                    e.AcceptedOperation = DataPackageOperation.None;
                }
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex);
            }
            finally
            {
                Deferral.Complete();
            }
        }

        private async void ItemContainer_Drop(object sender, DragEventArgs e)
        {
            DragOperationDeferral Deferral = e.GetDeferral();

            try
            {
                e.Handled = true;

                DelayEnterCancellation?.Cancel();

                IReadOnlyList<string> PathList = await e.DataView.GetAsPathListAsync();

                if (PathList.Count > 0)
                {
                    switch ((sender as SelectorItem).Content)
                    {
                        case FileSystemStorageFolder Folder:
                            {
                                if (e.AcceptedOperation.HasFlag(DataPackageOperation.Move))
                                {
                                    QueueTaskController.EnqueueMoveOpeartion(PathList, Folder.Path);
                                }
                                else
                                {
                                    QueueTaskController.EnqueueCopyOpeartion(PathList, Folder.Path);
                                }

                                break;
                            }
                        case FileSystemStorageFile File when File.Type.Equals(".exe", StringComparison.OrdinalIgnoreCase):
                            {
                                using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                                {
                                    if (!await Exclusive.Controller.RunAsync(File.Path, Path.GetDirectoryName(File.Path), WindowState.Normal, Parameters: PathList.ToArray()))
                                    {
                                        QueueContentDialog Dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_LaunchFailed_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        await Dialog.ShowAsync();
                                    }
                                }

                                break;
                            }
                    }
                }
            }
            catch (Exception ex) when (ex.HResult is unchecked((int)0x80040064) or unchecked((int)0x8004006A))
            {
                if ((sender as SelectorItem).Content is FileSystemStorageItemBase Item)
                {
                    QueueTaskController.EnqueueRemoteCopyOpeartion(Item.Path);
                }
            }
            catch
            {
                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_FailToGetClipboardError_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                await dialog.ShowAsync();
            }
            finally
            {
                Deferral.Complete();
            }
        }

        private void ViewControl_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                args.ItemContainer.AllowDrop = false;

                args.ItemContainer.DragStarting -= ItemContainer_DragStarting;
                args.ItemContainer.Drop -= ItemContainer_Drop;
                args.ItemContainer.DragOver -= ItemContainer_DragOver;
                args.ItemContainer.PointerEntered -= ItemContainer_PointerEntered;
                args.ItemContainer.PointerExited -= ItemContainer_PointerExited;
                args.ItemContainer.DragEnter -= ItemContainer_DragEnter;
                args.ItemContainer.PointerCanceled -= ItemContainer_PointerCanceled;
                args.ItemContainer.DragLeave -= ItemContainer_DragLeave;
            }
            else
            {
                switch (args.Item)
                {
                    case FileSystemStorageFolder:
                        {
                            args.ItemContainer.AllowDrop = true;
                            args.ItemContainer.DragEnter += ItemContainer_DragEnter;
                            args.ItemContainer.DragLeave += ItemContainer_DragLeave;
                            break;
                        }
                    case FileSystemStorageFile File when File.Type.Equals(".exe", StringComparison.OrdinalIgnoreCase):
                        {
                            args.ItemContainer.AllowDrop = true;
                            break;
                        }
                }

                args.ItemContainer.Drop += ItemContainer_Drop;
                args.ItemContainer.DragOver += ItemContainer_DragOver;
                args.ItemContainer.DragStarting += ItemContainer_DragStarting;
                args.ItemContainer.PointerEntered += ItemContainer_PointerEntered;
                args.ItemContainer.PointerExited += ItemContainer_PointerExited;
                args.ItemContainer.PointerCanceled += ItemContainer_PointerCanceled;

                args.RegisterUpdateCallback(async (s, e) =>
                {
                    if (e.Item is FileSystemStorageItemBase Item)
                    {
                        switch (TabViewContainer.Current.LayoutModeControl.ViewModeIndex)
                        {
                            case 0:
                            case 1:
                            case 2:
                                {
                                    Item.SetThumbnailMode(ThumbnailMode.ListView);
                                    break;
                                }
                            default:
                                {
                                    Item.SetThumbnailMode(ThumbnailMode.SingleItem);
                                    break;
                                }
                        }

                        await Item.LoadAsync().ConfigureAwait(false);
                    }
                });
            }
        }

        private void ItemContainer_DragLeave(object sender, DragEventArgs e)
        {
            DelayEnterCancellation?.Cancel();
        }

        private void ItemContainer_DragEnter(object sender, DragEventArgs e)
        {
            if (sender is SelectorItem Selector && Selector.Content is FileSystemStorageItemBase Item)
            {
                DelayEnterCancellation?.Cancel();
                DelayEnterCancellation?.Dispose();
                DelayEnterCancellation = new CancellationTokenSource();

                Task.Delay(2000).ContinueWith(async (task, input) =>
                {
                    try
                    {
                        if (input is CancellationToken Token && !Token.IsCancellationRequested)
                        {
                            await EnterSelectedItemAsync(Item);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogTracer.Log(ex, "An exception was thew in DelayEnterProcess");
                    }
                }, DelayEnterCancellation.Token, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        private async void ItemContainer_DragStarting(UIElement sender, DragStartingEventArgs args)
        {
            DragOperationDeferral Deferral = args.GetDeferral();

            try
            {
                DelayRenameCancellation?.Cancel();

                IReadOnlyList<FileSystemStorageItemBase> DragList = ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().ToList();

                foreach (FileSystemStorageItemBase Item in DragList)
                {
                    if (ItemPresenter.ContainerFromItem(Item) is SelectorItem SItem && SItem.ContentTemplateRoot.FindChildOfType<TextBox>() is TextBox NameEditBox)
                    {
                        NameEditBox.Visibility = Visibility.Collapsed;
                    }
                }

                await args.Data.SetupDataPackageAsync(DragList);
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex);
            }
            finally
            {
                Deferral.Complete();
            }
        }

        private async void ItemContainer_DragOver(object sender, DragEventArgs e)
        {
            DragOperationDeferral Deferral = e.GetDeferral();

            try
            {
                e.Handled = true;

                if (Container.BladeViewer.FindChildOfType<ScrollViewer>() is ScrollViewer Viewer)
                {
                    double XOffset = e.GetPosition(Container.BladeViewer).X;
                    double HorizontalRightScrollThreshold = Viewer.ActualWidth - 50;
                    double HorizontalLeftScrollThreshold = 50;

                    if (XOffset > HorizontalRightScrollThreshold)
                    {
                        Viewer.ChangeView(Viewer.HorizontalOffset + XOffset - HorizontalRightScrollThreshold, null, null, false);
                    }
                    else if (XOffset < HorizontalLeftScrollThreshold)
                    {
                        Viewer.ChangeView(Viewer.HorizontalOffset + XOffset - HorizontalLeftScrollThreshold, null, null, false);
                    }
                }

                switch ((sender as SelectorItem)?.Content)
                {
                    case FileSystemStorageFolder Folder:
                        {
                            if (await e.DataView.CheckIfContainsAvailableDataAsync())
                            {
                                if (e.Modifiers.HasFlag(DragDropModifiers.Control))
                                {
                                    e.AcceptedOperation = DataPackageOperation.Copy;
                                    e.DragUIOverride.Caption = $"{Globalization.GetString("Drag_Tip_CopyTo")} \"{Folder.Name}\"";
                                }
                                else
                                {
                                    e.AcceptedOperation = DataPackageOperation.Move;
                                    e.DragUIOverride.Caption = $"{Globalization.GetString("Drag_Tip_MoveTo")} \"{Folder.Name}\"";
                                }

                                e.DragUIOverride.IsContentVisible = true;
                                e.DragUIOverride.IsCaptionVisible = true;
                                e.DragUIOverride.IsGlyphVisible = true;
                            }
                            else
                            {
                                e.AcceptedOperation = DataPackageOperation.None;
                            }

                            break;
                        }
                    case FileSystemStorageFile File when File.Type.Equals(".exe", StringComparison.OrdinalIgnoreCase):
                        {
                            IReadOnlyList<string> PathArray = await e.DataView.GetAsPathListAsync();

                            if (PathArray.Any() && PathArray.All((Path) => !Path.Equals(File.Path, StringComparison.OrdinalIgnoreCase)))
                            {
                                e.AcceptedOperation = DataPackageOperation.Link;
                                e.DragUIOverride.Caption = Globalization.GetString("Drag_Tip_RunWith").Replace("{Placeholder}", $"\"{File.Name}\"");

                                e.DragUIOverride.IsContentVisible = true;
                                e.DragUIOverride.IsCaptionVisible = true;
                                e.DragUIOverride.IsGlyphVisible = true;
                            }
                            else
                            {
                                e.AcceptedOperation = DataPackageOperation.None;
                            }

                            break;
                        }
                    default:
                        {
                            e.AcceptedOperation = DataPackageOperation.None;
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex);
            }
            finally
            {
                Deferral.Complete();
            }
        }

        private void ItemContainer_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if ((sender as SelectorItem)?.Content is FileSystemStorageItemBase Item)
            {
                if (!SettingPage.IsDoubleClickEnabled
                    && ItemPresenter.SelectionMode != ListViewSelectionMode.Multiple
                    && !Container.BlockKeyboardShortCutInput
                    && !ItemPresenter.SelectedItems.Contains(Item)
                    && !e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)
                    && !e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift))
                {
                    DelaySelectionCancellation?.Cancel();
                    DelaySelectionCancellation?.Dispose();
                    DelaySelectionCancellation = new CancellationTokenSource();

                    Task.Delay(800).ContinueWith((task, input) =>
                    {
                        if (input is CancellationToken Token && !Token.IsCancellationRequested)
                        {
                            SelectedItem = Item;
                        }
                    }, DelaySelectionCancellation.Token, TaskScheduler.FromCurrentSynchronizationContext());
                }

                DelayTooltipCancellation?.Cancel();
                DelayTooltipCancellation?.Dispose();
                DelayTooltipCancellation = new CancellationTokenSource();

                Task.Delay(800).ContinueWith(async (task, input) =>
                {
                    if (input is CancellationToken Token && !Token.IsCancellationRequested)
                    {
                        TooltipFlyout.Hide();

                        using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                        {
                            TooltipFlyoutText.Text = await Exclusive.Controller.GetTooltipTextAsync(Item.Path);

                            if (!string.IsNullOrWhiteSpace(TooltipFlyoutText.Text)
                                && !Token.IsCancellationRequested
                                && !Container.BlockKeyboardShortCutInput
                                && !FileFlyout.IsOpen
                                && !FolderFlyout.IsOpen
                                && !EmptyFlyout.IsOpen
                                && !MixedFlyout.IsOpen
                                && !LinkItemFlyout.IsOpen)
                            {
                                PointerPoint Point = e.GetCurrentPoint(ItemPresenter);

                                TooltipFlyout.ShowAt(ItemPresenter, new FlyoutShowOptions
                                {
                                    Position = new Point(Point.Position.X, Point.Position.Y + 20),
                                    ShowMode = FlyoutShowMode.TransientWithDismissOnPointerMoveAway,
                                    Placement = FlyoutPlacementMode.RightEdgeAlignedTop
                                });
                            }
                        }
                    }
                }, DelayTooltipCancellation.Token, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        private void ItemContainer_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            DelaySelectionCancellation?.Cancel();
            DelayTooltipCancellation?.Cancel();
        }

        private void ItemContainer_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            DelayEnterCancellation?.Cancel();
            DelayRenameCancellation?.Cancel();
            DelaySelectionCancellation?.Cancel();
            DelayTooltipCancellation?.Cancel();
        }

        private async void ViewControl_Drop(object sender, DragEventArgs e)
        {
            DragOperationDeferral Deferral = e.GetDeferral();

            try
            {
                e.Handled = true;

                IReadOnlyList<string> PathList = await e.DataView.GetAsPathListAsync();

                if (PathList.Count > 0)
                {
                    if (e.AcceptedOperation.HasFlag(DataPackageOperation.Move))
                    {
                        if (PathList.All((Item) => Path.GetDirectoryName(Item) != CurrentFolder.Path))
                        {
                            QueueTaskController.EnqueueMoveOpeartion(PathList, CurrentFolder.Path);
                        }
                    }
                    else
                    {
                        QueueTaskController.EnqueueCopyOpeartion(PathList, CurrentFolder.Path);
                    }
                }
            }
            catch (Exception ex) when (ex.HResult is unchecked((int)0x80040064) or unchecked((int)0x8004006A))
            {
                QueueTaskController.EnqueueRemoteCopyOpeartion(CurrentFolder.Path);
            }
            catch
            {
                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_FailToGetClipboardError_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await dialog.ShowAsync();
            }
            finally
            {
                Deferral.Complete();
            }
        }

        private async void ViewControl_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState == HoldingState.Started)
            {
                e.Handled = true;
                Container.BlockKeyboardShortCutInput = true;

                try
                {
                    await HandleContextActionRequested(e, e.GetPosition((FrameworkElement)sender));
                }
                catch (Exception ex)
                {
                    LogTracer.Log(ex, "Could not execute the context action");
                }

                Container.BlockKeyboardShortCutInput = false;
            }
        }

        private async void MixDecompression_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (ItemPresenter.SelectedItems.Any((Item) => Item is LinkStorageFile))
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LinkIsNotAllowInMixZip_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                await Dialog.ShowAsync();
            }
            else
            {
                if (ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().All((Item) => Item.Type.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                                                                                                || Item.Type.Equals(".tar", StringComparison.OrdinalIgnoreCase)
                                                                                                || Item.Type.Equals(".tar.gz", StringComparison.OrdinalIgnoreCase)
                                                                                                || Item.Type.Equals(".tgz", StringComparison.OrdinalIgnoreCase)
                                                                                                || Item.Type.Equals(".tar.bz2", StringComparison.OrdinalIgnoreCase)
                                                                                                || Item.Type.Equals(".gz", StringComparison.OrdinalIgnoreCase)
                                                                                                || Item.Type.Equals(".bz2", StringComparison.OrdinalIgnoreCase)
                                                                                                || Item.Type.Equals(".rar", StringComparison.OrdinalIgnoreCase)))
                {
                    QueueTaskController.EnqueueDecompressionOpeartion(ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().Select((Item) => Item.Path), CurrentFolder.Path, (sender as FrameworkElement)?.Name == "MixDecompressIndie");
                }
            }
        }

        private async void MixCompression_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (ItemPresenter.SelectedItems.Any((Item) => Item is LinkStorageFile))
            {
                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LinkIsNotAllowInMixZip_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await dialog.ShowAsync();

                return;
            }

            CompressDialog Dialog = new CompressDialog();

            if ((await Dialog.ShowAsync()) == ContentDialogResult.Primary)
            {
                QueueTaskController.EnqueueCompressionOpeartion(Dialog.Type, Dialog.Algorithm, Dialog.Level, ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().Select((Item) => Item.Path), Path.Combine(CurrentFolder.Path, Dialog.FileName));
            }
        }

        private async void OpenInTerminal_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SQLite.Current.GetTerminalProfileByName(SettingPage.DefaultTerminalName) is TerminalProfile Profile)
            {
                using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                {
                    if (!await Exclusive.Controller.RunAsync(Profile.Path, string.Empty, WindowState.Normal, Profile.RunAsAdmin, false, false, Regex.Matches(Profile.Argument, "[^ \"]+|\"[^\"]*\"").Select((Mat) => Mat.Value.Contains("[CurrentLocation]") ? Mat.Value.Replace("[CurrentLocation]", CurrentFolder.Path) : Mat.Value).ToArray()))
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_LaunchFailed_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        await Dialog.ShowAsync();
                    }
                }
            }
        }

        private async void OpenFolderInNewTab_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem is FileSystemStorageFolder Folder)
            {
                await TabViewContainer.Current.CreateNewTabAsync(Folder.Path);
            }
        }

        private void NameLabel_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            TextBlock NameLabel = (TextBlock)sender;

            if (e.GetCurrentPoint(NameLabel).Properties.IsLeftButtonPressed && e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && SettingPage.IsDoubleClickEnabled)
            {
                if ((e.OriginalSource as FrameworkElement)?.DataContext is FileSystemStorageItemBase Item)
                {
                    if (SelectedItem == Item)
                    {
                        DelayRenameCancellation?.Cancel();
                        DelayRenameCancellation?.Dispose();
                        DelayRenameCancellation = new CancellationTokenSource();

                        Task.Delay(1200).ContinueWith((task, input) =>
                        {
                            if (input is CancellationToken Token && !Token.IsCancellationRequested)
                            {
                                NameLabel.Visibility = Visibility.Collapsed;

                                if ((NameLabel.Parent as FrameworkElement)?.FindName("NameEditBox") is TextBox EditBox)
                                {
                                    EditBox.BeforeTextChanging += EditBox_BeforeTextChanging;
                                    EditBox.PreviewKeyDown += EditBox_PreviewKeyDown;
                                    EditBox.LostFocus += EditBox_LostFocus;
                                    EditBox.Text = NameLabel.Text;
                                    EditBox.Visibility = Visibility.Visible;
                                    EditBox.Focus(FocusState.Programmatic);
                                }

                                Container.BlockKeyboardShortCutInput = true;
                            }
                        }, DelayRenameCancellation.Token, TaskScheduler.FromCurrentSynchronizationContext());
                    }
                }
            }
        }

        private void EditBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case VirtualKey.Enter:
                    {
                        e.Handled = true;
                        ItemPresenter.Focus(FocusState.Programmatic);
                        break;
                    }
            }
        }

        private void EditBox_BeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
        {
            if (args.NewText.Any((Item) => Path.GetInvalidFileNameChars().Contains(Item)))
            {
                args.Cancel = true;

                if ((sender.Parent as FrameworkElement).FindName("NameLabel") is TextBlock NameLabel)
                {
                    InvalidCharTip.Target = NameLabel;
                    InvalidCharTip.IsOpen = true;
                }
            }
        }

        private async void EditBox_LostFocus(object sender, RoutedEventArgs e)
        {
            TextBox NameEditBox = (TextBox)sender;

            NameEditBox.LostFocus -= EditBox_LostFocus;
            NameEditBox.PreviewKeyDown -= EditBox_PreviewKeyDown;
            NameEditBox.BeforeTextChanging -= EditBox_BeforeTextChanging;

            if ((NameEditBox?.Parent as FrameworkElement)?.FindName("NameLabel") is TextBlock NameLabel && NameEditBox.DataContext is FileSystemStorageItemBase CurrentEditItem)
            {
                try
                {
                    if (!FileSystemItemNameChecker.IsValid(NameEditBox.Text))
                    {
                        InvalidNameTip.Target = NameLabel;
                        InvalidNameTip.IsOpen = true;
                        return;
                    }

                    if (CurrentEditItem.Name == NameEditBox.Text)
                    {
                        return;
                    }

                    if (!CurrentEditItem.Name.Equals(NameEditBox.Text, StringComparison.OrdinalIgnoreCase) && await FileSystemStorageItemBase.CheckExistAsync(Path.Combine(CurrentFolder.Path, NameEditBox.Text)))
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_RenameExist_Content"),
                            PrimaryButtonText = Globalization.GetString("Common_Dialog_ContinueButton"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                        };

                        if (await Dialog.ShowAsync() != ContentDialogResult.Primary)
                        {
                            return;
                        }
                    }

                    try
                    {
                        string NewName = await CurrentEditItem.RenameAsync(NameEditBox.Text);

                        FileSystemStorageItemBase TargetItem = null;

                        for (int MaxSearchLimit = 0; MaxSearchLimit < 4; MaxSearchLimit++)
                        {
                            TargetItem = FileCollection.FirstOrDefault((Item) => Item.Name.Equals(NewName, StringComparison.OrdinalIgnoreCase));

                            if (TargetItem == null)
                            {
                                await Task.Delay(500);
                            }
                            else
                            {
                                SelectedItem = TargetItem;
                                ItemPresenter.ScrollIntoView(TargetItem);
                                break;
                            }
                        }
                    }
                    catch (FileLoadException)
                    {
                        QueueContentDialog LoadExceptionDialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_FileOccupied_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton"),
                        };

                        _ = await LoadExceptionDialog.ShowAsync();
                    }
                    catch (InvalidOperationException)
                    {
                        QueueContentDialog UnauthorizeDialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_UnauthorizedRenameFile_Content"),
                            PrimaryButtonText = Globalization.GetString("Common_Dialog_ConfirmButton"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                        };

                        await UnauthorizeDialog.ShowAsync();
                    }
                }
                catch
                {
                    QueueContentDialog UnauthorizeDialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnauthorizedRenameFile_Content"),
                        PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                    };

                    await UnauthorizeDialog.ShowAsync();
                }
                finally
                {
                    NameEditBox.Visibility = Visibility.Collapsed;
                    NameLabel.Visibility = Visibility.Visible;

                    Container.BlockKeyboardShortCutInput = false;
                }
            }
        }

        private void GetFocus_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ItemPresenter?.Focus(FocusState.Programmatic);
        }

        private async void OpenFolderInNewWindow_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem is FileSystemStorageFolder Folder)
            {
                string StartupArgument = Uri.EscapeDataString(JsonSerializer.Serialize(new List<string[]>
                {
                    new string[]{ Folder.Path }
                }));

                await Launcher.LaunchUriAsync(new Uri($"rx-explorer:{StartupArgument}"));
            }
        }

        private async void Undo_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (OperationRecorder.Current.IsNotEmpty)
            {
                await ExecuteUndoAsync();
            }
        }

        private void OrderByName_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();
            SortCollectionGenerator.SaveSortConfigOnPath(CurrentFolder.Path, SortTarget.Name, SortDesc.IsChecked ? SortDirection.Descending : SortDirection.Ascending);
        }

        private void OrderByTime_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();
            SortCollectionGenerator.SaveSortConfigOnPath(CurrentFolder.Path, SortTarget.ModifiedTime, SortDesc.IsChecked ? SortDirection.Descending : SortDirection.Ascending);
        }

        private void OrderByType_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();
            SortCollectionGenerator.SaveSortConfigOnPath(CurrentFolder.Path, SortTarget.Type, SortDesc.IsChecked ? SortDirection.Descending : SortDirection.Ascending);
        }

        private void OrderBySize_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();
            SortCollectionGenerator.SaveSortConfigOnPath(CurrentFolder.Path, SortTarget.Size, SortDesc.IsChecked ? SortDirection.Descending : SortDirection.Ascending);
        }

        private void SortDesc_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            PathConfiguration Config = SQLite.Current.GetPathConfiguration(CurrentFolder.Path);
            SortCollectionGenerator.SaveSortConfigOnPath(CurrentFolder.Path, Config.SortTarget.GetValueOrDefault(), SortDirection.Descending);
        }

        private void SortAsc_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            PathConfiguration Config = SQLite.Current.GetPathConfiguration(CurrentFolder.Path);
            SortCollectionGenerator.SaveSortConfigOnPath(CurrentFolder.Path, Config.SortTarget.GetValueOrDefault(), SortDirection.Ascending);
        }

        private void SortMenuFlyout_Opening(object sender, object e)
        {
            PathConfiguration Configuration = SQLite.Current.GetPathConfiguration(CurrentFolder.Path);

            if (Configuration.SortDirection == SortDirection.Ascending)
            {
                SortDesc.IsChecked = false;
                SortAsc.IsChecked = true;
            }
            else
            {
                SortAsc.IsChecked = false;
                SortDesc.IsChecked = true;
            }

            switch (Configuration.SortTarget)
            {
                case SortTarget.Name:
                    {
                        OrderByType.IsChecked = false;
                        OrderByTime.IsChecked = false;
                        OrderBySize.IsChecked = false;
                        OrderByName.IsChecked = true;
                        break;
                    }
                case SortTarget.Type:
                    {
                        OrderByTime.IsChecked = false;
                        OrderBySize.IsChecked = false;
                        OrderByName.IsChecked = false;
                        OrderByType.IsChecked = true;
                        break;
                    }
                case SortTarget.ModifiedTime:
                    {
                        OrderBySize.IsChecked = false;
                        OrderByName.IsChecked = false;
                        OrderByType.IsChecked = false;
                        OrderByTime.IsChecked = true;
                        break;
                    }
                case SortTarget.Size:
                    {
                        OrderByName.IsChecked = false;
                        OrderByType.IsChecked = false;
                        OrderByTime.IsChecked = false;
                        OrderBySize.IsChecked = true;
                        break;
                    }
            }
        }

        private async void BottomCommandBar_Opening(object sender, object e)
        {
            BottomCommandBar.PrimaryCommands.Clear();
            BottomCommandBar.SecondaryCommands.Clear();

            if (ItemPresenter.SelectedItems.Count > 1)
            {
                AppBarButton CopyButton = new AppBarButton
                {
                    IsTabStop = false,
                    Icon = new SymbolIcon(Symbol.Copy),
                    Label = Globalization.GetString("Operate_Text_Copy")
                };
                CopyButton.Click += Copy_Click;
                BottomCommandBar.PrimaryCommands.Add(CopyButton);

                AppBarButton CutButton = new AppBarButton
                {
                    IsTabStop = false,
                    Icon = new SymbolIcon(Symbol.Cut),
                    Label = Globalization.GetString("Operate_Text_Cut")
                };
                CutButton.Click += Cut_Click;
                BottomCommandBar.PrimaryCommands.Add(CutButton);

                AppBarButton DeleteButton = new AppBarButton
                {
                    IsTabStop = false,
                    Icon = new SymbolIcon(Symbol.Delete),
                    Label = Globalization.GetString("Operate_Text_Delete")
                };
                DeleteButton.Click += Delete_Click;
                BottomCommandBar.PrimaryCommands.Add(DeleteButton);
            }
            else
            {
                if (SelectedItem is FileSystemStorageItemBase Item)
                {
                    AppBarButton CopyButton = new AppBarButton
                    {
                        IsTabStop = false,
                        Icon = new SymbolIcon(Symbol.Copy),
                        Label = Globalization.GetString("Operate_Text_Copy")
                    };
                    CopyButton.Click += Copy_Click;
                    BottomCommandBar.PrimaryCommands.Add(CopyButton);

                    AppBarButton CutButton = new AppBarButton
                    {
                        IsTabStop = false,
                        Icon = new SymbolIcon(Symbol.Cut),
                        Label = Globalization.GetString("Operate_Text_Cut")
                    };
                    CutButton.Click += Cut_Click;
                    BottomCommandBar.PrimaryCommands.Add(CutButton);

                    AppBarButton DeleteButton = new AppBarButton
                    {
                        IsTabStop = false,
                        Icon = new SymbolIcon(Symbol.Delete),
                        Label = Globalization.GetString("Operate_Text_Delete")
                    };
                    DeleteButton.Click += Delete_Click;
                    BottomCommandBar.PrimaryCommands.Add(DeleteButton);

                    AppBarButton RenameButton = new AppBarButton
                    {
                        IsTabStop = false,
                        Icon = new SymbolIcon(Symbol.Rename),
                        Label = Globalization.GetString("Operate_Text_Rename")
                    };
                    RenameButton.Click += Rename_Click;
                    BottomCommandBar.PrimaryCommands.Add(RenameButton);
                }
                else
                {
                    AppBarButton MultiSelectButton = new AppBarButton
                    {
                        IsTabStop = false,
                        Icon = new FontIcon { Glyph = "\uE762" },
                        Label = Globalization.GetString("Operate_Text_MultiSelect")
                    };
                    MultiSelectButton.Click += MultiSelect_Click;
                    BottomCommandBar.PrimaryCommands.Add(MultiSelectButton);

                    bool EnablePasteButton;

                    try
                    {
                        EnablePasteButton = await Clipboard.GetContent().CheckIfContainsAvailableDataAsync();
                    }
                    catch
                    {
                        EnablePasteButton = false;
                    }

                    AppBarButton PasteButton = new AppBarButton
                    {
                        IsTabStop = false,
                        Icon = new SymbolIcon(Symbol.Paste),
                        Label = Globalization.GetString("Operate_Text_Paste"),
                        IsEnabled = EnablePasteButton
                    };
                    PasteButton.Click += Paste_Click;
                    BottomCommandBar.PrimaryCommands.Add(PasteButton);

                    AppBarButton UndoButton = new AppBarButton
                    {
                        IsTabStop = false,
                        Icon = new SymbolIcon(Symbol.Undo),
                        Label = Globalization.GetString("Operate_Text_Undo"),
                        IsEnabled = OperationRecorder.Current.IsNotEmpty
                    };
                    UndoButton.Click += Undo_Click;
                    BottomCommandBar.PrimaryCommands.Add(UndoButton);

                    AppBarButton RefreshButton = new AppBarButton
                    {
                        IsTabStop = false,
                        Icon = new SymbolIcon(Symbol.Refresh),
                        Label = Globalization.GetString("Operate_Text_Refresh")
                    };
                    RefreshButton.Click += Refresh_Click;
                    BottomCommandBar.PrimaryCommands.Add(RefreshButton);
                }
            }
        }

        private void ListHeader_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private async void LnkOpenLocation_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem is LinkStorageFile Item)
            {
                if (Item.LinkTargetPath == Globalization.GetString("UnknownText") || Item.LinkType == ShellLinkType.UWP)
                {
                    QueueContentDialog dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = $"{Globalization.GetString("QueueDialog_LocatePathFailure_Content")} {Environment.NewLine}\"{Item.LinkTargetPath}\"",
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton"),
                    };

                    await dialog.ShowAsync();
                }
                else
                {
                    string ParentFolderPath = Path.GetDirectoryName(Item.LinkTargetPath);

                    if (await DisplayItemsInFolder(ParentFolderPath))
                    {
                        if (FileCollection.FirstOrDefault((SItem) => SItem.Path.Equals(Item.LinkTargetPath, StringComparison.OrdinalIgnoreCase)) is FileSystemStorageItemBase Target)
                        {
                            ItemPresenter.ScrollIntoView(Target);
                            SelectedItem = Target;
                        }
                    }
                    else
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = $"{Globalization.GetString("QueueDialog_LocatePathFailure_Content")} {Environment.NewLine}\"{ParentFolderPath}\"",
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        await Dialog.ShowAsync();
                    }
                }
            }
        }

        private void MultiSelect_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (ItemPresenter.SelectionMode == ListViewSelectionMode.Extended)
            {
                ItemPresenter.SelectionMode = ListViewSelectionMode.Multiple;
            }
            else
            {
                ItemPresenter.SelectionMode = ListViewSelectionMode.Extended;
            }
        }

        private void ViewControl_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case VirtualKey.Space when e.OriginalSource is not TextBox:
                    {
                        e.Handled = true;
                        break;
                    }
            }
        }

        private void ListHeaderRelativePanel_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if ((sender as FrameworkElement).FindChildOfName<Button>("NameFilterHeader") is Button NameFilterBtn)
            {
                NameFilterBtn.Visibility = Visibility.Visible;
            }
            else if ((sender as FrameworkElement).FindChildOfName<Button>("ModTimeFilterHeader") is Button ModTimeFilterBtn)
            {
                ModTimeFilterBtn.Visibility = Visibility.Visible;
            }
            else if ((sender as FrameworkElement).FindChildOfName<Button>("TypeFilterHeader") is Button TypeFilterBtn)
            {
                TypeFilterBtn.Visibility = Visibility.Visible;
            }
            else if ((sender as FrameworkElement).FindChildOfName<Button>("SizeFilterHeader") is Button SizeFilterBtn)
            {
                SizeFilterBtn.Visibility = Visibility.Visible;
            }
        }

        private void ListHeaderRelativePanel_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if ((sender as FrameworkElement).FindChildOfName<Button>("NameFilterHeader") is Button NameFilterBtn)
            {
                if (!NameFilterBtn.Flyout.IsOpen)
                {
                    NameFilterBtn.Visibility = Visibility.Collapsed;
                }
            }
            else if ((sender as FrameworkElement).FindChildOfName<Button>("ModTimeFilterHeader") is Button ModTimeFilterBtn)
            {
                if (!ModTimeFilterBtn.Flyout.IsOpen)
                {
                    ModTimeFilterBtn.Visibility = Visibility.Collapsed;
                }
            }
            else if ((sender as FrameworkElement).FindChildOfName<Button>("TypeFilterHeader") is Button TypeFilterBtn)
            {
                if (!TypeFilterBtn.Flyout.IsOpen)
                {
                    TypeFilterBtn.Visibility = Visibility.Collapsed;
                }
            }
            else if ((sender as FrameworkElement).FindChildOfName<Button>("SizeFilterHeader") is Button SizeFilterBtn)
            {
                if (!SizeFilterBtn.Flyout.IsOpen)
                {
                    SizeFilterBtn.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void FilterFlyout_Closing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
        {
            Container.BlockKeyboardShortCutInput = false;

            if (sender.Target is FrameworkElement Element)
            {
                Element.Visibility = Visibility.Collapsed;
            }
        }

        private void FilterFlyout_Opened(object sender, object e)
        {
            Container.BlockKeyboardShortCutInput = true;
        }

        private void Filter_RefreshListRequested(object sender, FilterController.RefreshRequestedEventArgs args)
        {
            PathConfiguration Config = SQLite.Current.GetPathConfiguration(CurrentFolder.Path);

            FileCollection.Clear();
            GroupCollection.Clear();

            if (IsGroupedEnable)
            {
                foreach (FileSystemStorageGroupItem GroupItem in GroupCollectionGenerator.GetGroupedCollection(args.FilterCollection, Config.GroupTarget.GetValueOrDefault(), Config.GroupDirection.GetValueOrDefault()))
                {
                    GroupCollection.Add(new FileSystemStorageGroupItem(GroupItem.Key, SortCollectionGenerator.GetSortedCollection(GroupItem, Config.SortTarget.GetValueOrDefault(), Config.SortDirection.GetValueOrDefault())));
                }
            }

            foreach (FileSystemStorageItemBase Item in args.FilterCollection)
            {
                FileCollection.Add(Item);
            }
        }

        private async void OpenFolderInVerticalSplitView_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem != null)
            {
                await Container.CreateNewBladeAsync(SelectedItem.Path);
            }
        }

        private void DecompressionOptionFlyout_Opening(object sender, object e)
        {
            if (SelectedItem is FileSystemStorageFile File)
            {
                string DecompressionFolderName = File.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                                                                ? File.Name.Substring(0, File.Name.Length - 7)
                                                                : (File.Name.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase)
                                                                                        ? File.Name.Substring(0, File.Name.Length - 8)
                                                                                        : Path.GetFileNameWithoutExtension(File.Name));

                if (string.IsNullOrEmpty(DecompressionFolderName))
                {
                    DecompressionFolderName = Globalization.GetString("Operate_Text_CreateFolder");
                }

                DecompressionOption2.Text = $"{Globalization.GetString("DecompressTo")} \"{DecompressionFolderName}\\\"";

                ToolTipService.SetToolTip(DecompressionOption2, new ToolTip
                {
                    Content = DecompressionOption2.Text
                });
            }
        }

        private async void DecompressOption_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem is FileSystemStorageFile File)
            {
                CloseAllFlyout();

                if (!await FileSystemStorageItemBase.CheckExistAsync(File.Path))
                {
                    QueueContentDialog dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await dialog.ShowAsync();

                    return;
                }


                if (ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().All((Item) => Item.Type.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                                                                                                || Item.Type.Equals(".tar", StringComparison.OrdinalIgnoreCase)
                                                                                                || Item.Type.Equals(".tar.gz", StringComparison.OrdinalIgnoreCase)
                                                                                                || Item.Type.Equals(".tgz", StringComparison.OrdinalIgnoreCase)
                                                                                                || Item.Type.Equals(".tar.bz2", StringComparison.OrdinalIgnoreCase)
                                                                                                || Item.Type.Equals(".bz2", StringComparison.OrdinalIgnoreCase)
                                                                                                || Item.Type.Equals(".gz", StringComparison.OrdinalIgnoreCase)
                                                                                                || Item.Type.Equals(".rar", StringComparison.OrdinalIgnoreCase)))
                {
                    DecompressDialog Dialog = new DecompressDialog(Path.GetDirectoryName(File.Path));

                    if (await Dialog.ShowAsync() == ContentDialogResult.Primary)
                    {
                        FileSystemStorageFolder TargetFolder = await FileSystemStorageItemBase.CreateNewAsync(Path.Combine(Dialog.ExtractLocation, File.Name.Split(".")[0]), StorageItemTypes.Folder, CreateOption.GenerateUniqueName) as FileSystemStorageFolder;

                        if (TargetFolder == null)
                        {
                            QueueContentDialog dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_UnauthorizedDecompression_Content"),
                                PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                            };

                            await dialog.ShowAsync();
                        }
                        else
                        {
                            QueueTaskController.EnqueueDecompressionOpeartion(File.Path, TargetFolder.Path, false, Dialog.CurrentEncoding);
                        }
                    }
                }
            }
        }

        private async void MixDecompressOption_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (ItemPresenter.SelectedItems.Any((Item) => Item is LinkStorageFile))
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LinkIsNotAllowInMixZip_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync();

                return;
            }


            if (ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().All((Item) => Item.Type.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".tar", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".tar.gz", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".tgz", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".tar.bz2", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".bz2", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".gz", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".rar", StringComparison.OrdinalIgnoreCase)))
            {
                DecompressDialog Dialog = new DecompressDialog(CurrentFolder.Path);

                if (await Dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    QueueTaskController.EnqueueDecompressionOpeartion(ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().Select((Item) => Item.Path), Dialog.ExtractLocation, true, Dialog.CurrentEncoding);
                }
            }
        }

        private void UnTag_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            SelectedItem.ColorTag = ColorTag.Transparent;
        }


        private void MixUnTag_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            foreach (FileSystemStorageItemBase Item in ItemPresenter.SelectedItems)
            {
                Item.ColorTag = ColorTag.Transparent;
            }
        }

        private void Color_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (sender is AppBarButton Btn)
            {
                SelectedItem.ColorTag = Enum.Parse<ColorTag>(Convert.ToString(Btn.Tag));
            }
        }

        private void ColorTag_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement Element)
            {
                if (Element.FindParentOfType<AppBarElementContainer>() is AppBarElementContainer Container)
                {
                    Container.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void CommandBarFlyout_Closing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
        {
            if (sender is CommandBarFlyout Flyout)
            {
                foreach (FlyoutBase SubFlyout in Flyout.SecondaryCommands.OfType<AppBarButton>().Select((Btn) => Btn.Flyout).OfType<FlyoutBase>())
                {
                    SubFlyout.Hide();
                }
            }
        }

        private void CommandBarFlyout_Closed(object sender, object e)
        {
            if (sender is CommandBarFlyout Flyout)
            {
                if (Flyout.PrimaryCommands.OfType<AppBarElementContainer>().FirstOrDefault((Container) => !string.IsNullOrEmpty(Container.Name)) is AppBarElementContainer Container)
                {
                    Container.Visibility = Visibility.Visible;
                }
            }
        }

        private void ColorBarBack_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement Element)
            {
                if (Element.FindParentOfType<AppBarElementContainer>() is AppBarElementContainer Container)
                {
                    Container.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void MixColor_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();


            if (sender is AppBarButton Btn)
            {
                foreach (FileSystemStorageItemBase Item in ItemPresenter.SelectedItems)
                {
                    Item.ColorTag = Enum.Parse<ColorTag>(Convert.ToString(Btn.Tag));
                }
            }
        }

        private async void MixOpen_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (ItemPresenter.SelectedItems.Count > 0)
            {
                foreach (FileSystemStorageItemBase Item in ItemPresenter.SelectedItems)
                {
                    switch (Item)
                    {
                        case FileSystemStorageFolder Folder:
                            {
                                if (await MSStoreHelper.Current.CheckPurchaseStatusAsync())
                                {
                                    await Container.CreateNewBladeAsync(Folder.Path);
                                }
                                else
                                {
                                    await TabViewContainer.Current.CreateNewTabAsync(Folder.Path);
                                }

                                break;
                            }
                        case FileSystemStorageFile File:
                            {
                                await EnterSelectedItemAsync(File);
                                break;
                            }
                    }
                }
            }
        }

        private void GroupMenuFlyout_Opening(object sender, object e)
        {
            PathConfiguration Configuration = SQLite.Current.GetPathConfiguration(CurrentFolder.Path);

            if (Configuration.GroupDirection == GroupDirection.Ascending)
            {
                GroupDesc.IsChecked = false;
                GroupAsc.IsChecked = true;
            }
            else
            {
                GroupAsc.IsChecked = false;
                GroupDesc.IsChecked = true;
            }

            switch (Configuration.GroupTarget)
            {
                case GroupTarget.None:
                    {
                        GroupAsc.IsEnabled = false;
                        GroupDesc.IsEnabled = false;
                        GroupByType.IsChecked = false;
                        GroupByTime.IsChecked = false;
                        GroupBySize.IsChecked = false;
                        GroupByName.IsChecked = false;
                        GroupByNone.IsChecked = true;
                        break;
                    }
                case GroupTarget.Name:
                    {
                        GroupAsc.IsEnabled = true;
                        GroupDesc.IsEnabled = true;
                        GroupByType.IsChecked = false;
                        GroupByTime.IsChecked = false;
                        GroupBySize.IsChecked = false;
                        GroupByName.IsChecked = true;
                        GroupByNone.IsChecked = false;
                        break;
                    }
                case GroupTarget.Type:
                    {
                        GroupAsc.IsEnabled = true;
                        GroupDesc.IsEnabled = true;
                        GroupByTime.IsChecked = false;
                        GroupBySize.IsChecked = false;
                        GroupByName.IsChecked = false;
                        GroupByType.IsChecked = true;
                        GroupByNone.IsChecked = false;
                        break;
                    }
                case GroupTarget.ModifiedTime:
                    {
                        GroupAsc.IsEnabled = true;
                        GroupDesc.IsEnabled = true;
                        GroupBySize.IsChecked = false;
                        GroupByName.IsChecked = false;
                        GroupByType.IsChecked = false;
                        GroupByTime.IsChecked = true;
                        GroupByNone.IsChecked = false;
                        break;
                    }
                case GroupTarget.Size:
                    {
                        GroupAsc.IsEnabled = true;
                        GroupDesc.IsEnabled = true;
                        GroupByName.IsChecked = false;
                        GroupByType.IsChecked = false;
                        GroupByTime.IsChecked = false;
                        GroupBySize.IsChecked = true;
                        GroupByNone.IsChecked = false;
                        break;
                    }
            }
        }

        private void GroupByName_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();
            GroupCollectionGenerator.SavePathGroupState(CurrentFolder.Path, GroupTarget.Name, GroupAsc.IsChecked ? GroupDirection.Ascending : GroupDirection.Descending);
        }

        private void GroupByTime_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();
            GroupCollectionGenerator.SavePathGroupState(CurrentFolder.Path, GroupTarget.ModifiedTime, GroupAsc.IsChecked ? GroupDirection.Ascending : GroupDirection.Descending);
        }

        private void GroupByType_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();
            GroupCollectionGenerator.SavePathGroupState(CurrentFolder.Path, GroupTarget.Type, GroupAsc.IsChecked ? GroupDirection.Ascending : GroupDirection.Descending);
        }

        private void GroupBySize_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();
            GroupCollectionGenerator.SavePathGroupState(CurrentFolder.Path, GroupTarget.Size, GroupAsc.IsChecked ? GroupDirection.Ascending : GroupDirection.Descending);
        }

        private void GroupAsc_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();
            PathConfiguration Config = SQLite.Current.GetPathConfiguration(CurrentFolder.Path);
            GroupCollectionGenerator.SavePathGroupState(CurrentFolder.Path, Config.GroupTarget.GetValueOrDefault(), GroupDirection.Ascending);
        }

        private void GroupDesc_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();
            PathConfiguration Config = SQLite.Current.GetPathConfiguration(CurrentFolder.Path);
            GroupCollectionGenerator.SavePathGroupState(CurrentFolder.Path, Config.GroupTarget.GetValueOrDefault(), GroupDirection.Descending);
        }

        private void GroupNone_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();
            GroupCollectionGenerator.SavePathGroupState(CurrentFolder.Path, GroupTarget.None, GroupDirection.Ascending);
        }

        private async void RootFolderControl_EnterActionRequested(object sender, string Path)
        {
            if (!await DisplayItemsInFolder(Path))
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = $"{Globalization.GetString("QueueDialog_LocatePathFailure_Content")} {Environment.NewLine}\"{Path}\"",
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton"),
                };

                await Dialog.ShowAsync();
            }
        }

        private void SendToFlyout_Opening(object sender, object e)
        {
            if (sender is MenuFlyout Flyout)
            {
                foreach (MenuFlyoutItem Item in Flyout.Items)
                {
                    Item.Click -= SendToItem_Click;
                }

                Flyout.Items.Clear();

                MenuFlyoutItem SendDocumentItem = new MenuFlyoutItem
                {
                    Name = "SendDocumentItem",
                    Text = Globalization.GetString("SendTo_Document"),
                    Icon = new ImageIcon
                    {
                        Source = new BitmapImage(new Uri("ms-appx:///Assets/DocumentIcon.ico"))
                    },
                    MinWidth = 150,
                    MaxWidth = 350
                };
                SendDocumentItem.Click += SendToItem_Click;

                Flyout.Items.Add(SendDocumentItem);

                MenuFlyoutItem SendLinkItem = new MenuFlyoutItem
                {
                    Name = "SendLinkItem",
                    Text = Globalization.GetString("SendTo_CreateDesktopShortcut"),
                    Icon = new ImageIcon
                    {
                        Source = new BitmapImage(new Uri("ms-appx:///Assets/DesktopIcon.ico"))
                    },
                    MinWidth = 150,
                    MaxWidth = 350
                };
                SendLinkItem.Click += SendToItem_Click;

                Flyout.Items.Add(SendLinkItem);

                foreach (DriveDataBase RemovableDrive in CommonAccessCollection.DriveList.Where((Drive) => (Drive.DriveType is DriveType.Removable or DriveType.Network) && !string.IsNullOrEmpty(Drive.Path)).ToArray())
                {
                    MenuFlyoutItem SendRemovableDriveItem = new MenuFlyoutItem
                    {
                        Name = "SendRemovableItem",
                        Text = $"{(string.IsNullOrEmpty(RemovableDrive.DisplayName) ? RemovableDrive.Path : RemovableDrive.DisplayName)}",
                        Icon = new ImageIcon
                        {
                            Source = RemovableDrive.Thumbnail
                        },
                        MinWidth = 150,
                        MaxWidth = 350,
                        Tag = RemovableDrive.Path
                    };
                    SendRemovableDriveItem.Click += SendToItem_Click;

                    Flyout.Items.Add(SendRemovableDriveItem);
                }
            }
        }

        private async void SendToItem_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (sender is FrameworkElement Item && SelectedItem is FileSystemStorageItemBase SItem)
            {
                switch (Item.Name)
                {
                    case "SendLinkItem":
                        {
                            string DesktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

                            if (await FileSystemStorageItemBase.CheckExistAsync(DesktopPath))
                            {
                                using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                                {
                                    if (!await Exclusive.Controller.CreateLinkAsync(new LinkDataPackage
                                    {
                                        LinkPath = Path.Combine(DesktopPath, $"{(SItem is FileSystemStorageFolder ? SItem.Name : Path.GetFileNameWithoutExtension(SItem.Name))}.lnk"),
                                        LinkTargetPath = SItem.Path
                                    }))
                                    {
                                        QueueContentDialog Dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_UnauthorizedCreateNewFile_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        await Dialog.ShowAsync();
                                    }
                                }
                            }
                            else
                            {
                                try
                                {
                                    IReadOnlyList<User> UserList = await User.FindAllAsync();

                                    UserDataPaths DataPath = UserList.FirstOrDefault((User) => User.AuthenticationStatus == UserAuthenticationStatus.LocallyAuthenticated && User.Type == UserType.LocalUser) is User CurrentUser
                                                             ? UserDataPaths.GetForUser(CurrentUser)
                                                             : UserDataPaths.GetDefault();

                                    if (await FileSystemStorageItemBase.CheckExistAsync(DataPath.Desktop))
                                    {
                                        using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                                        {
                                            if (!await Exclusive.Controller.CreateLinkAsync(new LinkDataPackage
                                            {
                                                LinkPath = Path.Combine(DataPath.Desktop, $"{(SItem is FileSystemStorageFolder ? SItem.Name : Path.GetFileNameWithoutExtension(SItem.Name))}.lnk"),
                                                LinkTargetPath = SItem.Path
                                            }))
                                            {
                                                QueueContentDialog Dialog = new QueueContentDialog
                                                {
                                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                    Content = Globalization.GetString("QueueDialog_UnauthorizedCreateNewFile_Content"),
                                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                };

                                                await Dialog.ShowAsync();
                                            }
                                        }
                                    }
                                    else
                                    {
                                        LogTracer.Log($"Could not execute \"Send to\" command because desktop path \"{DataPath.Desktop}\" is not exists");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogTracer.Log(ex, "Could not get desktop path from UserDataPaths");
                                }
                            }

                            break;
                        }
                    case "SendDocumentItem":
                        {
                            string DocumentPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                            if (await FileSystemStorageItemBase.CheckExistAsync(DocumentPath))
                            {
                                QueueTaskController.EnqueueCopyOpeartion(SItem.Path, DocumentPath);
                            }
                            else
                            {
                                try
                                {
                                    IReadOnlyList<User> UserList = await User.FindAllAsync();

                                    UserDataPaths DataPath = UserList.FirstOrDefault((User) => User.AuthenticationStatus == UserAuthenticationStatus.LocallyAuthenticated && User.Type == UserType.LocalUser) is User CurrentUser
                                                             ? UserDataPaths.GetForUser(CurrentUser)
                                                             : UserDataPaths.GetDefault();

                                    if (await FileSystemStorageItemBase.CheckExistAsync(DataPath.Documents))
                                    {
                                        QueueTaskController.EnqueueCopyOpeartion(SItem.Path, DataPath.Documents);
                                    }
                                    else
                                    {
                                        LogTracer.Log($"Could not execute \"Send to\" command because document path \"{DataPath.Documents}\" is not exists");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogTracer.Log(ex, "Could not get document path from UserDataPaths");
                                }
                            }

                            break;
                        }
                    case "SendRemovableItem":
                        {
                            if (Item.Tag is string RemovablePath)
                            {
                                QueueTaskController.EnqueueCopyOpeartion(SItem.Path, RemovablePath);
                            }

                            break;
                        }
                }
            }
        }

        private async void StatusTips_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (Path.GetPathRoot(CurrentFolder?.Path).Equals(CurrentFolder?.Path, StringComparison.OrdinalIgnoreCase))
            {
                await DisplayItemsInFolder(RootStorageFolder.Instance);
            }
            else if (Container.GoParentFolder.IsEnabled)
            {
                Container.GoParentFolder_Click(null, null);
            }
        }

        private async void ExpandToCurrentFolder_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            TreeViewNode RootNode = Container.FolderTree.RootNodes.FirstOrDefault((Node) => (Node.Content as TreeViewNodeContent).Path.Equals(Path.GetPathRoot(CurrentFolder.Path), StringComparison.OrdinalIgnoreCase));

            if (RootNode != null)
            {
                TreeViewNode TargetNode = await RootNode.GetNodeAsync(new PathAnalysis(CurrentFolder.Path, string.Empty));
                Container.FolderTree.SelectNodeAndScrollToVertical(TargetNode);
            }
        }

        private void CreatNewFlyout_Opening(object sender, object e)
        {
            CreatNewFlyout.Items.Clear();

            MenuFlyoutItem FolderItem = new MenuFlyoutItem
            {
                Name = "FolderItem",
                Icon = new ImageIcon
                {
                    Source = new BitmapImage(WindowsVersionChecker.IsNewerOrEqual(Class.Version.Windows11)
                                                ? new Uri("ms-appx:///Assets/FolderIcon_Win11.png")
                                                : new Uri("ms-appx:///Assets/FolderIcon_Win10.png"))
                },
                Text = Globalization.GetString("Operate_Text_CreateFolder"),
                MinWidth = 160
            };
            FolderItem.Click += CreateFolder_Click;
            CreatNewFlyout.Items.Add(FolderItem);

            MenuFlyoutItem LinkItem = new MenuFlyoutItem
            {
                Name = "LinkItem",
                Icon = new ImageIcon
                {
                    Source = new BitmapImage(new Uri("ms-appx:///Assets/lnkFileIcon.png"))
                },
                Text = $"{Globalization.GetString("Link_Admin_DisplayType")} (.lnk)",
                MinWidth = 160
            };
            LinkItem.Click += CreateFile_Click;
            CreatNewFlyout.Items.Add(LinkItem);

            CreatNewFlyout.Items.Add(new MenuFlyoutSeparator());

            MenuFlyoutItem DocItem = new MenuFlyoutItem
            {
                Name = "DocItem",
                Icon = new ImageIcon
                {
                    Source = new BitmapImage(new Uri("ms-appx:///Assets/WordFileIcon.png"))
                },
                Text = "Microsoft Word (.docx)",
                MinWidth = 160
            };
            DocItem.Click += CreateFile_Click;
            CreatNewFlyout.Items.Add(DocItem);

            MenuFlyoutItem PPTItem = new MenuFlyoutItem
            {
                Name = "PPTItem",
                Icon = new ImageIcon
                {
                    Source = new BitmapImage(new Uri("ms-appx:///Assets/PowerPointFileIcon.png"))
                },
                Text = "Microsoft PowerPoint (.pptx)",
                MinWidth = 160
            };
            PPTItem.Click += CreateFile_Click;
            CreatNewFlyout.Items.Add(PPTItem);

            MenuFlyoutItem XLSItem = new MenuFlyoutItem
            {
                Name = "XLSItem",
                Icon = new ImageIcon
                {
                    Source = new BitmapImage(new Uri("ms-appx:///Assets/ExcelFileIcon.png"))
                },
                Text = "Microsoft Excel (.xlsx)",
                MinWidth = 160
            };
            XLSItem.Click += CreateFile_Click;
            CreatNewFlyout.Items.Add(XLSItem);

            MenuFlyoutItem RtfItem = new MenuFlyoutItem
            {
                Name = "RtfItem",
                Icon = new ImageIcon
                {
                    Source = new BitmapImage(new Uri("ms-appx:///Assets/RtfFileIcon.png"))
                },
                Text = $"{Globalization.GetString("File_Type_RTF_Description")} (.rtf)",
                MinWidth = 160
            };
            RtfItem.Click += CreateFile_Click;
            CreatNewFlyout.Items.Add(RtfItem);

            MenuFlyoutItem BmpItem = new MenuFlyoutItem
            {
                Name = "BmpItem",
                Icon = new ImageIcon
                {
                    Source = new BitmapImage(new Uri("ms-appx:///Assets/BmpFileIcon.png"))
                },
                Text = $"{Globalization.GetString("File_Type_Bmp_Description")} (.bmp)",
                MinWidth = 160
            };
            BmpItem.Click += CreateFile_Click;
            CreatNewFlyout.Items.Add(BmpItem);

            MenuFlyoutItem TxtItem = new MenuFlyoutItem
            {
                Name = "TxtItem",
                Icon = new ImageIcon
                {
                    Source = new BitmapImage(new Uri("ms-appx:///Assets/TxtFileIcon.png"))
                },
                Text = $"{Globalization.GetString("File_Type_TXT_Description")} (.txt)",
                MinWidth = 160
            };
            TxtItem.Click += CreateFile_Click;
            CreatNewFlyout.Items.Add(TxtItem);

            MenuFlyoutItem CompressItem = new MenuFlyoutItem
            {
                Name = "CompressItem",
                Icon = new ImageIcon
                {
                    Source = new BitmapImage(new Uri("ms-appx:///Assets/ZipFileIcon.png"))
                },
                Text = $"{Globalization.GetString("File_Type_Compress_Description")} (.zip)",
                MinWidth = 160
            };
            CompressItem.Click += CreateFile_Click;
            CreatNewFlyout.Items.Add(CompressItem);
        }

        private void CreatNewFlyout_Closed(object sender, object e)
        {
            foreach (MenuFlyoutItem Item in CreatNewFlyout.Items.OfType<MenuFlyoutItem>())
            {
                if (Item.Name == "FolderItem")
                {
                    Item.Click -= CreateFolder_Click;
                }
                else
                {
                    Item.Click -= CreateFile_Click;
                }
            }

            CreatNewFlyout.Items.Clear();
        }

        private void FileFlyout_Opening(object sender, object e)
        {
            if (ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().All((Item) => Item.Type.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".tar", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".tar.gz", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".tgz", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".tar.bz2", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".bz2", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".gz", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".rar", StringComparison.OrdinalIgnoreCase)))
            {
                Decompression.Visibility = Visibility.Visible;
            }
            else
            {
                Decompression.Visibility = Visibility.Collapsed;
            }
        }

        private void MixedFlyout_Opening(object sender, object e)
        {
            if (ItemPresenter.SelectedItems.Cast<FileSystemStorageItemBase>().All((Item) => Item.Type.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".tar", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".tar.gz", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".tgz", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".tar.bz2", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".gz", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".bz2", StringComparison.OrdinalIgnoreCase)
                                                                                            || Item.Type.Equals(".rar", StringComparison.OrdinalIgnoreCase)))
            {
                MixDecompression.Visibility = Visibility.Visible;
            }
            else
            {
                MixDecompression.Visibility = Visibility.Collapsed;
            }
        }

        public void Dispose()
        {
            FileCollection.Clear();
            GroupCollection.Clear();

            AreaWatcher.FileChanged -= DirectoryWatcher_FileChanged;
            AreaWatcher.Dispose();

            WiFiProvider?.Dispose();
            SelectionExtention?.Dispose();
            DelayRenameCancellation?.Dispose();
            DelayEnterCancellation?.Dispose();
            DelaySelectionCancellation?.Dispose();
            DelayTooltipCancellation?.Dispose();
            DelayDragCancellation?.Dispose();
            ContextMenuCancellation?.Dispose();
            EnterLock?.Dispose();
            CollectionChangeLock?.Dispose();

            AreaWatcher = null;
            WiFiProvider = null;
            SelectionExtention = null;
            DelayRenameCancellation = null;
            DelayEnterCancellation = null;
            DelaySelectionCancellation = null;
            DelayTooltipCancellation = null;
            DelayDragCancellation = null;
            ContextMenuCancellation = null;
            EnterLock = null;
            CollectionChangeLock = null;

            RecordIndex = 0;
            GoAndBackRecord.Clear();

            FileCollection.CollectionChanged -= FileCollection_CollectionChanged;
            ListViewDetailHeader.Filter.RefreshListRequested -= Filter_RefreshListRequested;
            RootFolderControl.EnterActionRequested -= RootFolderControl_EnterActionRequested;

            CoreWindow Window = CoreWindow.GetForCurrentThread();
            Window.KeyDown -= FilePresenter_KeyDown;
            Window.Dispatcher.AcceleratorKeyActivated -= Dispatcher_AcceleratorKeyActivated;

            Application.Current.Suspending -= Current_Suspending;
            Application.Current.Resuming -= Current_Resuming;
            SortCollectionGenerator.SortConfigChanged -= Current_SortConfigChanged;
            GroupCollectionGenerator.GroupStateChanged -= GroupCollectionGenerator_GroupStateChanged;
            LayoutModeController.ViewModeChanged -= Current_ViewModeChanged;
        }
    }
}

