﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

using WinQuickLook.Extensions;
using WinQuickLook.Handlers;
using WinQuickLook.Internal;
using WinQuickLook.Interop;

namespace WinQuickLook.Views
{
    public partial class QuickLookWindow
    {
        public QuickLookWindow()
        {
            InitializeComponent();
        }

        private string _fileName;

        private readonly DirectoryQuickLookHandler _directoryHandler = new();

        private readonly IQuickLookHandler[] _fileHandlers =
        {
            new HtmlQuickLookHandler(),
            new SyntaxHighlightQuickLookHandler(),
            new TextQuickLookHandler(),
            new InternetShortcutQuickLookHandler(),
            new PdfQuickLookHandler(),
            new VideoQuickLookHandler(),
            new AudioQuickLookHandler(),
            new AnimatedGifQuickLookHandler(),
            new SvgQuickLookHandler(),
            new ImageQuickLookHandler(),
            new ComInteropQuickLookHandler()
        };

        public FrameworkElement PreviewHost
        {
            get => (FrameworkElement)GetValue(PreviewHostProperty);
            set => SetValue(PreviewHostProperty, value);
        }

        public static readonly DependencyProperty PreviewHostProperty =
            DependencyProperty.Register(nameof(PreviewHost), typeof(FrameworkElement), typeof(QuickLookWindow), new PropertyMetadata(null));

        public static readonly RoutedUICommand OpenWithAssoc = new();

        public bool HideIfVisible()
        {
            if (!IsVisible)
            {
                return false;
            }

            Hide();
            CleanupHost();

            return true;
        }

        public void Open(string fileName)
        {
            CleanupHost();

            _fileName = fileName;

            FrameworkElement element;
            Size requestSize;
            string metadata;

            if (File.Exists(fileName))
            {
                var fileInfo = new FileInfo(fileName);

                var handler = _fileHandlers.FirstOrDefault(x => x.CanOpen(fileInfo));

                (element, requestSize, metadata) = handler.GetViewerWithHandleError(fileInfo);
            }
            else if (Directory.Exists(fileName))
            {
                var directoryInfo = new DirectoryInfo(fileName);

                (element, requestSize, metadata) = _directoryHandler.GetViewer(directoryInfo);
            }
            else
            {
                return;
            }

            PreviewHost = element;

            Title = $"{Path.GetFileName(fileName)}{(metadata == null ? "" : $" ({metadata})")}";

            SetAssociatedAppName(fileName);
            MoveWindowCentering(requestSize);

            Topmost = true;

            Show();

            Topmost = false;
        }

        private void CleanupHost()
        {
            if (PreviewHost is WindowsFormsHost formsHost)
            {
                formsHost.Child.Dispose();
                formsHost.Child = null;

                formsHost.Dispose();
            }

            PreviewHost = null;
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            InitializeWindowStyle();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);

            hwndSource.AddHook(WndProc);
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (PreviewHost is Image image && image.StretchDirection != StretchDirection.Both)
            {
                image.StretchDirection = StretchDirection.Both;
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            e.Cancel = true;
        }

        private void OpenWithListButton_Click(object sender, RoutedEventArgs e)
        {
            var contextMenu = openWithListButton.ContextMenu;

            contextMenu.PlacementTarget = openWithListButton;
            contextMenu.IsOpen = true;
        }

        private void OpenWithButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(_fileName) { UseShellExecute = true });

                HideIfVisible();
            }
            catch
            {
                MessageBox.Show(Strings.Resources.OpenButtonErrorMessage, "WinQuickLook");
            }
        }

        private void OpenCommandBinding_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            try
            {
                AssocHandlerHelper.Invoke((string)e.Parameter, _fileName);

                HideIfVisible();
            }
            catch
            {
                MessageBox.Show(Strings.Resources.OpenButtonErrorMessage, "WinQuickLook");
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            HideIfVisible();
        }

        private void SetAssociatedAppName(string fileName)
        {
            var assocName = AssocHandlerHelper.GetAssocName(fileName);

            if (string.IsNullOrEmpty(assocName))
            {
                openWithButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                ((TextBlock)openWithButton.Content).Text = string.Format(Strings.Resources.OpenButtonText, assocName);

                openWithButton.Visibility = Visibility.Visible;
            }

            var assocAppList = AssocHandlerHelper.GetAssocAppList(fileName);

            if (assocAppList.Count == 0)
            {
                openWithListButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                var contextMenu = openWithListButton.ContextMenu;

                contextMenu.ItemsSource = assocAppList;

                openWithListButton.Visibility = Visibility.Visible;
            }
        }

        private void InitializeWindowStyle()
        {
            var theme = PlatformHelper.GetWindowsTheme();

            Foreground = theme == WindowsTheme.Light ? Brushes.Black : Brushes.LightGray;

            var accentPolicy = new ACCENTPOLICY
            {
                nAccentState = 3,
                nFlags = 2,
                nColor = theme == WindowsTheme.Light ? 0xC0FFFFFF : 0xC0000000
            };

            var accentPolicySize = Marshal.SizeOf(accentPolicy);
            var accentPolicyPtr = Marshal.AllocHGlobal(accentPolicySize);

            Marshal.StructureToPtr(accentPolicy, accentPolicyPtr, false);

            var winCompatData = new WINCOMPATTRDATA
            {
                nAttribute = 19,
                ulDataSize = accentPolicySize,
                pData = accentPolicyPtr
            };

            var hwnd = new WindowInteropHelper(this).Handle;

            NativeMethods.SetWindowCompositionAttribute(hwnd, ref winCompatData);

            Marshal.FreeHGlobal(accentPolicyPtr);

            var style = NativeMethods.GetWindowLong(hwnd, Consts.GWL_STYLE);
            NativeMethods.SetWindowLong(hwnd, Consts.GWL_STYLE, style & ~(Consts.WS_SYSMENU | Consts.WS_MINIMIZEBOX | Consts.WS_MAXIMIZEBOX));
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case Consts.WM_SYSKEYDOWN when wParam.ToInt32() == Consts.VK_F4:
                    handled = true;
                    break;
                case Consts.WM_NCRBUTTONUP when wParam.ToInt32() == Consts.HTCAPTION:
                    handled = true;
                    break;
            }

            return IntPtr.Zero;
        }

        private void MoveWindowCentering(Size requestSize)
        {
            var foregroundHwnd = NativeMethods.GetForegroundWindow();

            var hMonitor = NativeMethods.MonitorFromWindow(foregroundHwnd, Consts.MONITOR_DEFAULTTOPRIMARY);

            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

            NativeMethods.GetMonitorInfo(hMonitor, ref monitorInfo);

            var monitor = new Rect(monitorInfo.rcMonitor.x, monitorInfo.rcMonitor.y,
                monitorInfo.rcMonitor.cx - monitorInfo.rcMonitor.x, monitorInfo.rcMonitor.cy - monitorInfo.rcMonitor.y);

            NativeMethods.GetDpiForMonitor(hMonitor, Consts.MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY);

            var dpiFactorX = dpiX / 96.0;
            var dpiFactorY = dpiY / 96.0;

            var minWidthOrHeight = Math.Min(monitor.Width, monitor.Height) * 0.8;
            var scaleFactor = Math.Min(minWidthOrHeight / Math.Max(requestSize.Width, requestSize.Height), 1.0);

            Width = Math.Max(Math.Round(requestSize.Width * scaleFactor) + 10, MinWidth);
            Height = Math.Max(Math.Round(requestSize.Height * scaleFactor) + 40 + 5, MinHeight);

            var x = monitor.X + ((monitor.Width - (Width * dpiFactorX)) / 2);
            var y = monitor.Y + ((monitor.Height - (Height * dpiFactorY)) / 2);

            var hwnd = new WindowInteropHelper(this).Handle;

            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y), 0, 0, Consts.SWP_NOACTIVATE | Consts.SWP_NOSIZE | Consts.SWP_NOZORDER);
        }
    }
}
