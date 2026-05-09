using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using static Avalonia.X11.XLib;

namespace Avalonia.X11
{
    internal class XEmbedTrayIconImpl : ITrayIconImpl, INativeMenuExporter
    {
        private readonly AvaloniaX11Platform _platform;
        private readonly Func<IWindowIconImpl?, uint[]> _x11IconConverter;

        private IntPtr _iconWindow;
        private X11IconData? _iconData;
        private NativeMenu? _menu;
        private string? _tooltipText;
        private int _w, _h;
        private TrayPopupRoot? _trayMenu;

        public XEmbedTrayIconImpl(AvaloniaX11Platform platform, Func<IWindowIconImpl?, uint[]> x11IconConverter)
        {
            _platform = platform;
            _x11IconConverter = x11IconConverter;
            var dpy = platform.Info.Display;

            var screen = platform.Info.DefaultScreen;
            var root = platform.Info.RootWindow;

            var tray_sel = XInternAtom(dpy, $"_NET_SYSTEM_TRAY_S{screen}", false);

            var tray_manager = XGetSelectionOwner(dpy, tray_sel);
            if (tray_manager == 0)
            {
                Debug.WriteLine($"No system tray manager found on screen {screen}");
                return;
            }

            _iconWindow = XCreateSimpleWindow(
                dpy, root,
                x: 0, y: 0, width: 16, height: 16, // will be overwritten by tray anyway
                0,
                0,
                0
            );

            XSelectInput(dpy, _iconWindow, (nint)(XEventMask.ExposureMask | XEventMask.ButtonPressMask | XEventMask.StructureNotifyMask));

            X11Window.SetWmClass(platform.Info, _iconWindow, "AvaloniaTrayIcon");

            unsafe
            {
                var bytes = "AvaloniaTrayIcon"u8;
                fixed (void* titles = bytes)
                {
                    XChangeProperty(
                        dpy, _iconWindow,
                        platform.Info.Atoms._NET_WM_NAME,
                        platform.Info.Atoms.UTF8_STRING, 8, PropertyMode.Replace,
                        titles, bytes.Length);
                }
            }

            // initial state
            SetIsVisible(false);

            platform.Windows[_iconWindow] = WndProc;

            // do the docking
            XEvent ev = new()
            {
                ClientMessageEvent = new XClientMessageEvent()
                {
                    type = XEventName.ClientMessage,
                    window = _iconWindow,
                    message_type = platform.Info.Atoms._NET_SYSTEM_TRAY_OPCODE,
                    format = 32,
                    ptr1 = 0, // CurrentTime
                    ptr2 = (IntPtr)SystrayRequest.SYSTEM_TRAY_REQUEST_DOCK,
                    ptr3 = _iconWindow,
                    ptr4 = 0,
                    ptr5 = 0,
                }
            };

            XSendEvent(dpy, tray_manager, false, (IntPtr)EventMask.NoEventMask, ref ev);
        }

        public Action? OnClicked { get; set; }

        public INativeMenuExporter MenuExporter => this;

        public void Dispose()
        {
            _trayMenu?.Close();
            var window = _iconWindow;
            _iconWindow = IntPtr.Zero;

            if (window == IntPtr.Zero) return;
            _platform.Windows.Remove(window);
            XDestroyWindow(_platform.Info.Display, window);
        }

        public void SetIcon(IWindowIconImpl? icon)
        {
            _iconData = icon as X11IconData;
            XClearArea(_platform.Display, _iconWindow, 0, 0, 0, 0, true);
        }

        public void SetToolTipText(string? text)
        {
            _tooltipText = text;
        }

        public void SetIsVisible(bool visible)
        {
            var data = new[] { IntPtr.Zero, new(visible ? 1 : 0) /* XEMBED_MAPPED */ };
            XChangeProperty(_platform.Display, _iconWindow, _platform.Info.Atoms._XEMBED_INFO, _platform.Info.Atoms._XEMBED_INFO, 32, PropertyMode.Replace, data, data.Length);
        }

        private unsafe void WndProc(ref XEvent xev)
        {
            if (_iconWindow == 0)
            {
                return;
            }

            if (xev.type == XEventName.ButtonPress)
            {
                var button = xev.ButtonEvent.button;

                if (button == 1)
                    OnClicked?.Invoke();
                else if (button == 3)
                    OnRightClicked(xev.ButtonEvent.x_root, xev.ButtonEvent.y_root);
                return;
            }

            bool redraw = false;
            if (xev.type == XEventName.ConfigureNotify)
            {
                _w = xev.ConfigureEvent.width;
                _h = xev.ConfigureEvent.height;
                redraw = true;
            }
            else if (xev.type == XEventName.Expose)
            {
                XClearWindow(_platform.Display, _iconWindow);
                if (_iconData == null || _w == 0 || _h == 0)
                {
                    return;
                }

                redraw = true;
            }

            if (redraw)
            {
                var x11IconConverter = _x11IconConverter(_iconData);
                var w = (int)x11IconConverter[0];
                var h = (int)x11IconConverter[1];

                var trayIconSize = new PixelSize(_w, _h);
                uint[] scaledImage = new uint[h * w];
                fixed (void* ptr = &x11IconConverter[2])
                fixed (void* pPixels = scaledImage)
                {
                    var scaledBitmap =
                        new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, new IntPtr(ptr), new PixelSize(w, h), new Vector(96, 96), w * 4)
                            .CreateScaledBitmap(trayIconSize);
                    scaledBitmap.CopyPixels(new LockedFramebuffer((IntPtr)pPixels, trayIconSize, trayIconSize.Width * 4,
                        new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul, null));
                }

                var gc = XCreateGC(_platform.Display, _iconWindow, 0, IntPtr.Zero);
                try
                {
                    fixed (void* addr = scaledImage)
                    {
                        var image = new XImage();
                        int bitsPerPixel = 4 * 8;
                        image.width = trayIconSize.Width;
                        image.height = trayIconSize.Height;
                        image.format = 2; //ZPixmap;
                        image.data = (IntPtr)addr;
                        image.byte_order = 0; // LSBFirst;
                        image.bitmap_unit = bitsPerPixel;
                        image.bitmap_bit_order = 0; // LSBFirst;
                        image.bitmap_pad = bitsPerPixel;
                        image.depth = 24;
                        image.bytes_per_line = trayIconSize.Width * 4;
                        image.bits_per_pixel = bitsPerPixel;
                        XInitImage(ref image);
                        XPutImage(_platform.Display, _iconWindow, gc, ref image, 0, 0, 0, 0, (uint)trayIconSize.Width, (uint)trayIconSize.Height);
                    }
                }
                finally
                {
                    XFreeGC(_platform.Display, gc);
                }
            }
        }

        public void SetNativeMenu(NativeMenu? menu) => _menu = menu;

        private void OnRightClicked(int x, int y)
        {
            var menu = _menu;
            if (menu == null || menu.Items.Count == 0)
            {
                return;
            }

            _trayMenu?.Close();
            _trayMenu = new TrayPopupRoot(this, new X11Window(_platform, null, true))
            {
                Name = "AvaloniaTrayPopupRoot_" + _tooltipText,
                WindowDecorations = WindowDecorations.None,
                SizeToContent = SizeToContent.WidthAndHeight,
                Background = null,
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
                Content = new TrayIconMenuFlyoutPresenter() { ItemsSource = menu.Items }
            };

            if (_trayMenu.PlatformImpl is X11Window x11Window)
            {
                x11Window.SetNetWmWindowType(X11NetWmWindowType.Dialog);
            }

            _trayMenu.Show();

            _trayMenu.Position = new PixelPoint(x, y);
        }

        private class TrayPopupRoot : Window
        {
            private readonly XEmbedTrayIconImpl _xEmbedTrayIconImpl;
            private readonly ManagedPopupPositioner _positioner;
            private readonly TrayIconManagedPopupPositionerPopupImplHelper _positionerHelper;

            public TrayPopupRoot(XEmbedTrayIconImpl xEmbedTrayIconImpl, IWindowImpl window)
                : base(window)
            {
                _xEmbedTrayIconImpl = xEmbedTrayIconImpl;
                _positionerHelper = new TrayIconManagedPopupPositionerPopupImplHelper(MoveResize);
                _positioner = new ManagedPopupPositioner(_positionerHelper);
                Topmost = true;

                Deactivated += TrayPopupRoot_Deactivated;

                ShowInTaskbar = false;

                ShowActivated = true;
            }

            private void TrayPopupRoot_Deactivated(object? sender, EventArgs e)
            {
                Close();
            }

            protected override void OnClosed(EventArgs e)
            {
                base.OnClosed(e);
                _positionerHelper.Dispose();
                if (_xEmbedTrayIconImpl._trayMenu == this)
                {
                    _xEmbedTrayIconImpl._trayMenu = null;
                }
            }

            private void MoveResize(PixelPoint position, Size size, double scaling)
            {
                if (PlatformImpl is { } platformImpl)
                {
                    platformImpl.Move(position);
                    platformImpl.Resize(size, WindowResizeReason.Layout);
                }
            }

            protected override void ArrangeCore(Rect finalRect)
            {
                base.ArrangeCore(finalRect);

                _positioner.Update(new PopupPositionerParameters
                {
                    Anchor = PopupAnchor.TopLeft,
                    Gravity = PopupGravity.BottomRight,
                    AnchorRectangle = new Rect(Position.ToPoint(Screens.Primary?.Scaling ?? 1.0), new Size(1, 1)),
                    Size = finalRect.Size,
                    ConstraintAdjustment = PopupPositionerConstraintAdjustment.FlipX | PopupPositionerConstraintAdjustment.FlipY,
                });
            }

            private class TrayIconManagedPopupPositionerPopupImplHelper : IManagedPopupPositionerPopup, IDisposable
            {
                private readonly Action<PixelPoint, Size, double> _moveResize;
                private readonly Window _hiddenWindow;

                public TrayIconManagedPopupPositionerPopupImplHelper(Action<PixelPoint, Size, double> moveResize)
                {
                    _moveResize = moveResize;
                    _hiddenWindow = new Window();
                }

                public IReadOnlyList<ManagedPopupPositionerScreenInfo> Screens =>
                    _hiddenWindow.Screens.All
                        .Select(s => new ManagedPopupPositionerScreenInfo(s.Bounds.ToRect(1), s.Bounds.ToRect(1)))
                        .ToArray();

                public Rect ParentClientAreaScreenGeometry
                {
                    get
                    {
                        return default;
                    }
                }

                public void MoveAndResize(Point devicePoint, Size virtualSize)
                {
                    _moveResize(new PixelPoint((int)devicePoint.X, (int)devicePoint.Y), virtualSize, Scaling);
                }

                public void Dispose()
                {
                    _hiddenWindow.Close();
                }

                public double Scaling => _hiddenWindow.Screens.Primary?.Scaling ?? 1.0;
            }
        }

        private class TrayIconMenuFlyoutPresenter : MenuFlyoutPresenter
        {
            public TrayIconMenuFlyoutPresenter() : base(new X11TrayIconMenuInteractionHandler(true))
            {
            }

            protected override Type StyleKeyOverride => typeof(MenuFlyoutPresenter);

            public override void Close()
            {
                // DefaultMenuInteractionHandler calls this
                var host = this.FindLogicalAncestorOfType<TrayPopupRoot>();
                if (host != null)
                {
                    SelectedIndex = -1;
                    host.Close();
                }
            }

            protected internal override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
            {
                return NativeMenuBarPresenter.CreateContainerForNativeItem(item, index, recycleKey)
                       ?? base.CreateContainerForItemOverride(item, index, recycleKey);
            }

            private class X11TrayIconMenuInteractionHandler : DefaultMenuInteractionHandler
            {
                private IDisposable? _cancel;

                public X11TrayIconMenuInteractionHandler(bool isContextMenu) : base(isContextMenu)
                {
                }

                protected internal override void PointerEntered(object? sender, RoutedEventArgs e)
                {
                    _cancel?.Dispose();
                    _cancel = null;
                    base.PointerEntered(sender, e);
                }

                protected internal override void PointerExited(object? sender, RoutedEventArgs e)
                {
                    _cancel?.Dispose();
                    _cancel = DispatcherTimer.RunOnce(() => Menu?.Close(), TimeSpan.FromMilliseconds(400));
                    base.PointerExited(sender, e);
                }
            }
        }
    }
}
